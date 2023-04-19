using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure.Maps.Search;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.GuessGame;

namespace SeattleCarsInBikeLanes.Models.GuessGame
{
    public class GuessGameState : IDisposable
    {
        private const int CodeLength = 9;
        private const int MaxScorePerRound = 100;

        public IHubContext<GuessGameHub>? Hub { private get; set; }
        public string Code { get; private set; }
        public int NumberOfRounds { get; private set; }
        public TimeSpan RoundLength { get; private set; }
        public DateTime? CurrentRoundEndTime { get; private set; }
        public int Round { get; private set; }
        public List<ReportedItem> Images { get; private set; }
        public ImageInfo? CurrentImageInfo { get; private set; }
        public string? AdminConnectionId { get; private set; }
        public ConcurrentBag<string> Users { get; private set; }
        public ConcurrentDictionary<string, string> ConnectionIdToUser { get; private set; }
        public ConcurrentDictionary<string, int> Scores { get; private set; }
        public ConcurrentDictionary<string, (double latitude, double longitude)> Guesses { get; private set; }
        public ConcurrentDictionary<string, bool> LockIns { get; private set; }

        private MapsSearchClient? mapsSearchClient = null;
        private HelperMethods? helperMethods = null;

        private CancellationTokenSource roundTimerCancellationTokenSource;
        private CancellationTokenSource countdownCancellationTokenSource;
        private Task? countdownTimerTask;
        private DateTime? countdownEndTime;

        public GuessGameState(int numberOfRounds)
        {
            StringBuilder codeBuilder = new StringBuilder();
            for (int i = 0; i < CodeLength; i++)
            {
                codeBuilder.Append(RandomNumberGenerator.GetInt32(10));
            }
            Code = codeBuilder.ToString();
            NumberOfRounds = numberOfRounds;
            RoundLength = TimeSpan.FromMinutes(1);
            Round = 0;
            Images = new List<ReportedItem>(NumberOfRounds);
            AdminConnectionId = null;
            Users = new ConcurrentBag<string>();
            ConnectionIdToUser = new ConcurrentDictionary<string, string>();
            Scores = new ConcurrentDictionary<string, int>();
            Guesses = new ConcurrentDictionary<string, (double latitude, double longitude)>();
            LockIns = new ConcurrentDictionary<string, bool>();

            roundTimerCancellationTokenSource = new CancellationTokenSource();
            countdownCancellationTokenSource = new CancellationTokenSource();
        }

        public async Task StartGame(ReportedItemsDatabase reportedItemsDatabase,
            MapsSearchClient mapsSearchClient,
            HelperMethods helperMethods,
            string connectionId)
        {
            if (connectionId == AdminConnectionId && Round == 0)
            {
                this.mapsSearchClient = mapsSearchClient;
                this.helperMethods = helperMethods;
                await SelectImages(reportedItemsDatabase);
            }
        }

        public void StartCountdown(string connectionId, string type, int seconds)
        {
            if (connectionId == AdminConnectionId)
            {
                if (Hub == null)
                {
                    throw new Exception("Game must be started before countdown can be started.");
                }

                countdownCancellationTokenSource.Dispose();
                countdownCancellationTokenSource = new CancellationTokenSource();
                
                countdownEndTime = DateTime.UtcNow.AddSeconds(seconds);
                countdownTimerTask = Task.Run(async () =>
                {
                    bool sentZero = false;
                    while (DateTime.UtcNow < countdownEndTime)
                    {
                        int secondsRemaining = (int)(countdownEndTime.Value - DateTime.UtcNow).TotalSeconds;
                        if (secondsRemaining == 0)
                        {
                            sentZero = true;
                        }
                        await Hub.Clients.Group(Code).SendAsync("ReceiveCountdown", type, secondsRemaining);
                        if (DateTime.UtcNow < countdownEndTime)
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1), countdownCancellationTokenSource.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }
                        }
                    }

                    if (!sentZero)
                    {
                        await Hub.Clients.Group(Code).SendAsync("ReceiveCountdown", type, 0);
                    }
                }, countdownCancellationTokenSource.Token);
            }
        }

        public IList<PlayerScoreInfo> GetPlayersWithScores()
        {
            var players = new List<PlayerScoreInfo>();
            foreach (var pair in ConnectionIdToUser)
            {
                string connectionId = pair.Key;
                string username = pair.Value;
                int score = Scores.GetOrAdd(connectionId, 0);
                players.Add(new PlayerScoreInfo()
                {
                    Username = username,
                    Score = score
                });
            }
            return players;
        }

        public RoundInfo GetRoundInfo()
        {
            int secondsRemaining = 0;
            if (CurrentRoundEndTime.HasValue)
            {
                secondsRemaining = (int)(CurrentRoundEndTime.Value - DateTime.UtcNow).TotalSeconds;
                if (secondsRemaining < 0)
                {
                    secondsRemaining = 0;
                }
            }
            return new RoundInfo()
            {
                Round = Round,
                NumberOfRounds = NumberOfRounds,
                RoundLength = secondsRemaining,
                RoundEndTime = CurrentRoundEndTime
            };
        }

        public async Task StartRound(string connectionId)
        {
            if (connectionId == AdminConnectionId)
            {
                if (Hub == null)
                {
                    throw new Exception("Game must be started before round can be started.");
                }

                if (Round == NumberOfRounds)
                {
                    return;
                }

                Guesses.Clear();
                roundTimerCancellationTokenSource.Dispose();
                roundTimerCancellationTokenSource = new CancellationTokenSource();
                Round += 1;
                ImageInfo imageInfo = await GetCurrentRoundImage();
                CurrentImageInfo = imageInfo;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(RoundLength, roundTimerCancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Do nothing.
                    }

                    if (roundTimerCancellationTokenSource.Token.IsCancellationRequested)
                    {
                        return;
                    }
                    await EndRound();
                }, roundTimerCancellationTokenSource.Token);
                CurrentRoundEndTime = DateTime.UtcNow.Add(RoundLength);
                RoundInfo roundInfo = new RoundInfo()
                {
                    Round = Round,
                    NumberOfRounds = NumberOfRounds,
                    RoundLength = (int)RoundLength.TotalSeconds,
                    RoundEndTime = CurrentRoundEndTime
                };
                _ = Hub.Clients.Group(Code).SendAsync("StartedRound", roundInfo);
                _ = Hub.Clients.Group(Code).SendAsync("ReceiveImage", imageInfo);
            }
        }

        private async Task EndRound()
        {
            if (Hub == null)
            {
                throw new Exception("Game must be started before round can be ended.");
            }

            LockIns.Clear();
            CurrentRoundEndTime = null;
            var endRoundInfo = CalculateRoundScores();
            await Hub.Clients.Group(Code).SendAsync("EndRound", endRoundInfo);
        }

        public void AddUser(string connectionId, string username)
        {
            if (ConnectionIdToUser.IsEmpty && Users.IsEmpty)
            {
                AdminConnectionId = connectionId;
            }
            ConnectionIdToUser[connectionId] = username;
            Users.Add(username);
            Scores[connectionId] = 0;
        }

        public bool ContainsUser(string connectionId)
        {
            foreach (var id in ConnectionIdToUser)
            {
                if (id.Key == connectionId)
                {
                    return true;
                }
            }
            return false;
        }

        public async void RemoveUser(string connectionId)
        {
            if (connectionId == AdminConnectionId)
            {
                if (Hub != null)
                {
                    await Hub.Clients.Group(Code).SendAsync("AdminLeftGame");
                }
            }
            string username = ConnectionIdToUser[connectionId];
            ConcurrentBag<string> newUsers = new ConcurrentBag<string>();
            while (!Users.IsEmpty)
            {
                if (Users.TryTake(out var user))
                {
                    if (user != username)
                    {
                        newUsers.Add(user);
                    }
                }
            }
            // Don't set Users to newUsers because between the while and Users assignment a new user could be added
            // to Users
            foreach (var user in newUsers)
            {
                Users.Add(user);
            }
            bool removed = false;
            while (!removed)
            {
                removed = Scores.TryRemove(connectionId, out _);
            }
            removed = false;
            while (!removed)
            {
                removed = ConnectionIdToUser.TryRemove(connectionId, out _);
            }
        }

        public async Task<ImageInfo> GetCurrentRoundImage()
        {
            if (helperMethods == null || mapsSearchClient == null)
            {
                throw new Exception("Cannot get round image as round has not been started");
            }
            ReportedItem item = Images[Round - 1];
            string url = item.ImgurUrls[0];
            var reverseSearch = await helperMethods.ReverseSearchCrossStreet(item.Location!.Position, mapsSearchClient);
            if (reverseSearch == null)
            {
                return new ImageInfo()
                {
                    ImageUrl = url,
                    Type = ImageInfo.Gps
                };
            }
            string[] splitPosition = reverseSearch.Position.Split(',');
            Position reverseSearchPosition = new Position(double.Parse(splitPosition[1]), double.Parse(splitPosition[0]));
            double distance = item.Location.DistanceTo(reverseSearchPosition);
            if (distance <= 10)
            {
                return new ImageInfo()
                {
                    ImageUrl = url,
                    Type = ImageInfo.Intersection
                };
            }
            else
            {
                return new ImageInfo()
                {
                    ImageUrl = url,
                    Type = ImageInfo.Gps
                };
            }
        }

        public void Guess(string connectionId, double latitude, double longitude)
        {
            if (!LockIns.ContainsKey(connectionId))
            {
                Guesses[connectionId] = (latitude, longitude);
            }
        }

        public async Task LockIn(string connectionId)
        {
            LockIns[connectionId] = true;
            bool allLockedIn = true;
            if (LockIns.Count == Users.Count)
            {
                foreach (var lockIn in LockIns)
                {
                    allLockedIn &= lockIn.Value;
                }
            }
            
            if (LockIns.Count == Users.Count && allLockedIn)
            {
                countdownCancellationTokenSource.Cancel();
                roundTimerCancellationTokenSource.Cancel();
                LockIns.Clear();
                await EndRound();
            }
        }

        private async Task SelectImages(ReportedItemsDatabase reportedItemsDatabase)
        {
            List<ReportedItem>? items = await reportedItemsDatabase.GetAllItems();
            if (items == null || items.Count == 0)
            {
                throw new Exception("No items found in database. Unable to start game.");
            }

            for (int i = 0; i < NumberOfRounds; i++)
            {
                Images.Add(SelectImage(items));
            }
        }

        private ReportedItem SelectImage(List<ReportedItem> items)
        {
            ReportedItem? selectedItem;
            // Keep track of how many times we failed to select an image. After a given number of tries just pick the
            // next image. Doesn't matter how close it is to other images.
            int tries = 10;
            do
            {
                int i = RandomNumberGenerator.GetInt32(items.Count);
                selectedItem = items[i];
                if (selectedItem.Location == null)
                {
                    selectedItem = null;
                    continue;
                }

                // Only use items with 1 image because the UI currently doesn't support displaying more than 1 image.
                if (selectedItem.ImgurUrls.Count != 1)
                {
                    selectedItem = null;
                    continue;
                }

                if (tries <= 0)
                {
                    continue;
                }

                foreach (var image in Images)
                {
                    // If the two locations are the same or are within 300 meters of each other (about 3 blocks)
                    if (selectedItem!.Location == image.Location || selectedItem.Location!.DistanceTo(image.Location!) <= 300)
                    {
                        selectedItem = null;
                        tries -= 1;
                        break;
                    }
                }
            }
            while (selectedItem == null);

            return selectedItem;
        }

        private RoundEndInfo CalculateRoundScores()
        {
            if (CurrentImageInfo == null)
            {
                throw new Exception("Current image info cannot be null when round has ended");
            }
            ReportedItem currentItem = Images[Round - 1];
            var distances = new List<(string id, double distance, double latitude, double longitude)>();
            foreach (var guess in Guesses)
            {
                Position guessPosition = new Position(guess.Value.longitude, guess.Value.latitude);
                distances.Add((guess.Key,
                    guessPosition.DistanceTo(currentItem.Location!.Position),
                    guess.Value.latitude,
                    guess.Value.longitude));
            }

            distances.Sort((a, b) =>
            {
                return a.distance.CompareTo(b.distance);
            });

            var scoreThisRound = new List<PlayerScoreInfo>();
            var distanceThisRound = new List<PlayerDistanceInfo>();
            foreach (var distance in distances)
            {
                int score = 0;
                if (distance.distance <= 1)
                {
                    score = MaxScorePerRound;
                }
                else
                {
                    // Function derived from https://github.com/GeoGuess/GeoGuess/blob/1c0c569d2e4bf1af9377fbe136a3fd42adbaf612/src/utils/game/score.js#L20
                    score = (int)Math.Round(MaxScorePerRound * Math.Exp(-(distance.distance / 1000 / 1)));
                    // 2258 is the magic number where adding 1 to the score will make all distance scores really far
                    // away will be at least 1 but distances near this number will not get a worse score even though
                    // they were closer.
                    if (distance.distance > 2258)
                    {
                        score += 1;
                    }
                }
                var currentScore = Scores.GetOrAdd(distance.id, 0);
                var updatedScore = currentScore + score;
                Scores.TryUpdate(distance.id, updatedScore, currentScore);

                string username = ConnectionIdToUser.GetOrAdd(distance.id, string.Empty);
                scoreThisRound.Add(new PlayerScoreInfo()
                {
                    Username = username,
                    Score = score
                });
                distanceThisRound.Add(new PlayerDistanceInfo()
                {
                    Username = username,
                    Latitude = distance.latitude,
                    Longitude = distance.longitude,
                    Distance = distance.distance
                });
            }

            return new RoundEndInfo()
            {
                GameOver = Round >= NumberOfRounds,
                Latitude = currentItem.Location!.Position.Latitude,
                Longitude = currentItem.Location!.Position.Longitude,
                Scores = scoreThisRound,
                Distances = distanceThisRound
            };
        }

        public void Dispose()
        {
            roundTimerCancellationTokenSource.Dispose();
            countdownCancellationTokenSource.Dispose();
        }

        public record ImageInfo
        {
            public const string Gps = "gps";
            public const string Intersection = "intersection";
            public required string ImageUrl { get; set; }
            public required string Type { get; set; }
        }

        public record RoundInfo
        {
            public required int Round { get; set; }
            public required int NumberOfRounds { get; set; }
            public required int RoundLength { get; set; }
            public required DateTime? RoundEndTime { get; set; }
        }

        public record PlayerScoreInfo
        {
            public required string Username { get; set; }
            public required int Score { get; set; }
        }

        public record PlayerDistanceInfo
        {
            public required string Username { get; set; }
            public required double Latitude { get; set; }
            public required double Longitude { get; set; }
            public required double Distance { get; set; }
        }

        public record RoundEndInfo
        {
            public required bool GameOver { get; set; }
            public required double Latitude { get; set; }
            public required double Longitude { get; set; }
            public required List<PlayerScoreInfo> Scores { get; set; }
            public required List<PlayerDistanceInfo> Distances { get; set; }
        }
    }
}
