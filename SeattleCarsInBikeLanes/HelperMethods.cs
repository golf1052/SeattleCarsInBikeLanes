using LinqToTwitter.Common;
using LinqToTwitter;
using SeattleCarsInBikeLanes.Database.Models;
using Azure.Maps.Search;
using Azure.Core.GeoJson;
using System.Runtime.InteropServices;
using Microsoft.Azure.Cosmos.Spatial;

namespace SeattleCarsInBikeLanes
{
    public class HelperMethods
    {
        public const string TwitterUsername = "carbikelanesea";
        public const string TwitterUserId = "1565182292695269377";

        public string GetTweetUrl(string username, string tweetId)
        {
            return $"https://twitter.com/{username}/status/{tweetId}";
        }

        public async Task<List<ReportedItem>?> TweetToReportedItem(Tweet tweet,
            List<TwitterMedia>? allMedia,
            MapsSearchClient mapsSearchClient)
        {
            if (string.IsNullOrWhiteSpace(tweet.ID) || tweet.CreatedAt == null || string.IsNullOrWhiteSpace(tweet.Text))
            {
                return null;
            }

            List<string>? reportedBlocks = GetReportedBlocks(tweet.Text);
            if (reportedBlocks == null)
            {
                return null;
            }

            List<ReportedItem> reportedItems = new List<ReportedItem>();
            int reportedItemCount = 0;
            foreach (string block in reportedBlocks)
            {
                string[] splitBlock = block.Split('\n');
                string[] splitFirstLine = splitBlock[0].Split(' ');
                int numberOfCars;
                if (!int.TryParse(splitFirstLine[0], out numberOfCars))
                {
                    continue;
                }

                string? dateString = GetReportedBlockValue(splitBlock[1..], "Date");
                DateOnly? date = null;
                if (!string.IsNullOrWhiteSpace(dateString))
                {
                    date = DateOnly.Parse(dateString);
                }

                string? timeString = GetReportedBlockValue(splitBlock[1..], "Time");
                TimeOnly? time = null;
                if (!string.IsNullOrWhiteSpace(timeString))
                {
                    time = TimeOnly.Parse(timeString);
                }

                string? locationString = GetReportedBlockValue(splitBlock[1..], "Location");
                if (string.IsNullOrEmpty(locationString))
                {
                    continue;
                }

                string? gpsString = GetReportedBlockValue(splitBlock[1..], "GPS");
                Point? location = null;
                if (!string.IsNullOrWhiteSpace(gpsString))
                {
                    string[] splitGpsString = gpsString.Split(',');
                    if (splitGpsString.Length == 2)
                    {
                        double latitude;
                        double longitude;
                        if (double.TryParse(splitGpsString[0].Trim(), out latitude) &&
                            double.TryParse(splitGpsString[1].Trim(), out longitude))
                        {
                            location = new Point(longitude, latitude);
                        }
                    }
                }
                else if (location == null)
                {
                    var potentialLocation = await GeocodeAddress(locationString, mapsSearchClient);
                    if (potentialLocation != null)
                    {
                        location = new Point(potentialLocation.Value.Longitude, potentialLocation.Value.Latitude);
                    }
                }

                ReportedItem reportedItem = new ReportedItem()
                {
                    TweetId = $"{tweet.ID}.{reportedItemCount}",
                    CreatedAt = tweet.CreatedAt.Value,
                    NumberOfCars = numberOfCars,
                    LocationString = locationString,
                    Location = location
                };

                if (date != null)
                {
                    // If the date exists it represents just the date of the incident
                    reportedItem.Date = date.Value.ToDateTime(new TimeOnly(), DateTimeKind.Utc);

                    if (time != null)
                    {
                        // If the time exists it represents just the time of the incident but we also set the date
                        // just in case. Since the time is reported as Pacific Time we need to convert it to UTC
                        // for storage.
                        TimeZoneInfo timeZoneInfo;
                        DateTimeOffset dateTimeOffset;
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                        }
                        else
                        {
                            timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
                        }
                        reportedItem.Time = date.Value.ToDateTime(time.Value, DateTimeKind.Unspecified);
                        dateTimeOffset = TimeZoneInfo.ConvertTimeToUtc(reportedItem.Time.Value, timeZoneInfo);
                        reportedItem.Time = dateTimeOffset.UtcDateTime;
                    }
                }

                // Finally check for any images and add them
                if (tweet.Attachments != null && tweet.Attachments.MediaKeys != null)
                {
                    foreach (var mediaKey in tweet.Attachments.MediaKeys)
                    {
                        string? url = GetUrlForMediaKey(mediaKey, allMedia);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            reportedItem.ImageUrls.Add(url);
                        }
                    }
                }

                reportedItems.Add(reportedItem);
                reportedItemCount += 1;
            }

            return reportedItems;
        }

        public async Task<GeoPosition?> GeocodeAddress(string locationString, MapsSearchClient mapsSearchClient)
        {
            SearchAddressOptions options = new SearchAddressOptions()
            {
                Language = "en-US",
                CountryFilter = new string[] { "US" },
                Coordinates = new GeoPosition(-122.333301, 47.606501)
            };
            var result = await mapsSearchClient.SearchAddressAsync(locationString, options);
            if (result == null || result.Value.NumResults == null || result.Value.NumResults.Value == 0)
            {
                return null;
            }
            var results = result.Value.Results.Where(r => r.Address.Municipality.Equals("Seattle", StringComparison.InvariantCultureIgnoreCase))
                .OrderByDescending(r => r.Score)
                .ToList();
            return results[0].Position;
        }

        public List<string>? GetReportedBlocks(string tweetText)
        {
            // First strip the ending Twitter shortlink
            var index = tweetText.LastIndexOf("https://");
            string cleanedTweetText = tweetText;
            if (index != -1)
            {
                cleanedTweetText = tweetText.Substring(0, index).Trim();
            }

            string[] splitTweet = cleanedTweetText.Split('\n');
            if (splitTweet.Length == 0)
            {
                return null;
            }

            // Next check that this is a report tweet
            string[] splitFirstLine = splitTweet[0].Split(' ');
            if (splitFirstLine.Length != 2 || !int.TryParse(splitFirstLine[0], out _) ||
                !splitFirstLine[1].StartsWith("car", StringComparison.InvariantCultureIgnoreCase))
            {
                return null;
            }

            // Now get each "reported block" where a block is separated by 2 new lines
            List<string> blocks = new List<string>();
            List<string> currentBlock = new List<string>();
            foreach (var line in splitTweet)
            {
                if (line.Length > 0)
                {
                    currentBlock.Add(line);
                }
                else
                {
                    blocks.Add(string.Join("\n", currentBlock));
                    currentBlock.Clear();
                }
            }

            if (currentBlock.Count > 0)
            {
                blocks.Add(string.Join("\n", currentBlock));
            }
            return blocks;
        }

        public string? GetReportedBlockValue(string[] lines, string key)
        {
            foreach (var line in lines)
            {
                int firstSpaceIndex = line.IndexOf(' ');
                if (firstSpaceIndex == -1)
                {
                    continue;
                }

                string lineKey = line.Substring(0, firstSpaceIndex);
                if (lineKey.StartsWith(key, StringComparison.InvariantCultureIgnoreCase))
                {
                    return line.Substring(firstSpaceIndex).Trim();
                }
            }
            return null;
        }

        public string? GetUrlForMediaKey(string mediaKey, List<TwitterMedia>? media)
        {
            if (media == null)
            {
                return null;
            }
            if (media.Count == 0)
            {
                return null;
            }

            foreach (var item in media)
            {
                if (item.MediaKey != null && item.MediaKey == mediaKey)
                {
                    return item.Url;
                }
            }
            return null;
        }

        public string GetRealTweetId(ReportedItem item)
        {
            return item.TweetId.Split('.')[0];
        }
    }
}
