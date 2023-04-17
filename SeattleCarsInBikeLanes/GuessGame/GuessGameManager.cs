using System.Collections.Concurrent;
using Azure.Maps.Search;
using Microsoft.AspNetCore.SignalR;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Models.GuessGame;
using static SeattleCarsInBikeLanes.Models.GuessGame.GuessGameState;

namespace SeattleCarsInBikeLanes.GuessGame
{
    public class GuessGameManager
    {
        private readonly ILogger<GuessGameManager> logger;
        private readonly ConcurrentDictionary<string, GuessGameState> gameStates;
        private readonly ReportedItemsDatabase reportedItemsDatabase;
        private readonly MapsSearchClient mapsSearchClient;
        private readonly HelperMethods helperMethods;
        private readonly IHubContext<GuessGameHub> hub;

        public GuessGameManager(ILogger<GuessGameManager> logger,
            ReportedItemsDatabase reportedItemsDatabase,
            MapsSearchClient mapsSearchClient,
            HelperMethods helperMethods,
            IHubContext<GuessGameHub> hub)
        {
            this.logger = logger;
            gameStates = new ConcurrentDictionary<string, GuessGameState>();
            this.reportedItemsDatabase = reportedItemsDatabase;
            this.mapsSearchClient = mapsSearchClient;
            this.helperMethods = helperMethods;
            this.hub = hub;
        }

        public void AddGame(GuessGameState game)
        {
            game.Hub = hub;
            gameStates[game.Code] = game;
        }

        public bool ContainsGame(string gameCode)
        {
            return gameStates.ContainsKey(gameCode);
        }

        public bool AddUser(string gameCode, string connectionId, string username)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                gameState.AddUser(connectionId, username);
                return true;
            }
            return false;
        }

        public void RemoveUser(string connectionId)
        {
            string? emptyGame = null;
            string? adminGame = null;
            foreach (var game in gameStates)
            {
                if (game.Value.ContainsUser(connectionId))
                {
                    if (game.Value.AdminConnectionId == connectionId)
                    {
                        adminGame = game.Key;
                    }
                    game.Value.RemoveUser(connectionId);
                    if (game.Value.Users.IsEmpty)
                    {
                        emptyGame = game.Key;
                    }
                    break;
                }
            }
            
            if (emptyGame != null)
            {
                logger.LogInformation($"Removing empty game {emptyGame}");
                RemoveGame(emptyGame);
            }
            else if (adminGame != null)
            {
                logger.LogInformation($"Removing admin left game {adminGame}");
                RemoveGame(adminGame);
            }
        }

        private void RemoveGame(string game)
        {
            bool removed = false;
            while (!removed)
            {
                removed = gameStates.TryRemove(game, out GuessGameState? state);
                if (removed && state != null)
                {
                    state.Dispose();
                }
            }
        }

        public IList<PlayerScoreInfo> GetPlayersWithScores(string gameCode)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                return gameState.GetPlayersWithScores();
            }
            else
            {
                return Array.Empty<PlayerScoreInfo>();
            }
        }

        public RoundInfo? GetRoundInfo(string gameCode)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                return gameState.GetRoundInfo();
            }
            else
            {
                return null;
            }
        }

        public async Task StartGame(string gameCode, string connectionId)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                await gameState.StartGame(
                    reportedItemsDatabase,
                    mapsSearchClient,
                    helperMethods,
                    connectionId);
            }
        }

        public void StartCountdown(string gameCode, string connectionId, string type, int seconds)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                gameState.StartCountdown(connectionId, type, seconds);
            }
        }

        public async Task StartRound(string gameCode, string connectionId)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                await gameState.StartRound(connectionId);
            }
        }

        public ImageInfo? GetRoundImage(string gameCode)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                return gameState.CurrentImageInfo;
            }
            else
            {
                return null;
            }
        }

        public void Guess(string gameCode, string connectionId, double latitude, double longitude)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                gameState.Guess(connectionId, latitude, longitude);
            }
        }

        public async Task LockIn(string gameCode, string connectionId)
        {
            if (gameStates.TryGetValue(gameCode, out GuessGameState? gameState))
            {
                await gameState.LockIn(connectionId);
            }
        }
    }
}
