using System.Text;
using Azure.Core.GeoJson;
using Azure.Maps.Search;
using Azure.Maps.Search.Models;
using Azure.Security.KeyVault.Secrets;
using HtmlAgilityPack;
using LinqToTwitter;
using LinqToTwitter.Common;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes
{
    public class HelperMethods
    {
        public const string TwitterUsername = "carbikelanesea";
        public const string TwitterUserId = "1565182292695269377";

        public bool IsAuthorized(AdminRequest request, SecretClient secretClient)
        {
            return IsAuthorized("admin-password", request.Password, secretClient);
        }

        public bool IsAuthorized(string secretName, string secretValue, SecretClient secretClient)
        {
            KeyVaultSecret keyVaultSecret = secretClient.GetSecret(secretName);
            string actualSecretValue = keyVaultSecret.Value;
            return actualSecretValue == secretValue;
        }

        public string GetTweetUrl(string username, string tweetId)
        {
            return $"https://twitter.com/{username}/status/{tweetId}";
        }

        public async Task<List<ReportedItem>?> TweetToReportedItems(Tweet tweet,
            List<TwitterMedia>? allMedia,
            MapsSearchClient mapsSearchClient)
        {
            if (string.IsNullOrWhiteSpace(tweet.ID) || tweet.CreatedAt == null || string.IsNullOrWhiteSpace(tweet.Text))
            {
                return null;
            }

            List<ReportedItem>? reportedItems = await TextToReportedItems(tweet.Text, mapsSearchClient);
            if (reportedItems == null)
            {
                return null;
            }

            foreach (var reportedItem in reportedItems)
            {
                reportedItem.CreatedAt = tweet.CreatedAt.Value;
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
            }

            return reportedItems;
        }

        public async Task<List<ReportedItem>?> TextToReportedItems(string text, MapsSearchClient mapsSearchClient)
        {
            List<string>? reportedBlocks = GetReportedBlocks(text);
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

                if (locationString.Contains("&amp;"))
                {
                    locationString = locationString.Replace("&amp;", "&");
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

                string id = Guid.NewGuid().ToString();
                ReportedItem reportedItem = new ReportedItem()
                {
                    TweetId = $"{id}.{reportedItemCount}",
                    NumberOfCars = numberOfCars,
                    Date = date,
                    Time = time,
                    LocationString = locationString,
                    Location = location
                };

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
            bool crossStreet = false;
            string[] splitLocationString = locationString.Split(' ');
            foreach (var str in splitLocationString)
            {
                if (str.Equals("and", StringComparison.InvariantCultureIgnoreCase) || str.Equals("&"))
                {
                    crossStreet = true;
                    break;
                }
            }
            var result = await mapsSearchClient.SearchAddressAsync(locationString, options);
            if (result == null || result.Value.NumResults == null || result.Value.NumResults.Value == 0)
            {
                return null;
            }

            var originalResults = result.Value.Results
                .Where(r => r.Address.Municipality.Equals("Seattle", StringComparison.InvariantCultureIgnoreCase))
                .OrderByDescending(r => r.Score)
                .ToList();

            var justCrossStreets = originalResults
                .Where(r =>
                {
                    if (crossStreet)
                    {
                        return r.SearchAddressResultType == SearchAddressResultType.CrossStreet;
                    }
                    else
                    {
                        return true;
                    }
                })
                .ToList();

            if (crossStreet && justCrossStreets.Count > 0)
            {
                return justCrossStreets[0].Position;
            }
            return originalResults[0].Position;
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
            if (splitFirstLine.Length < 2 || !int.TryParse(splitFirstLine[0], out _) ||
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
            if (item.TwitterLink == null)
            {
                return item.TweetId.Split('.')[0];
            }
            else
            {
                return new Uri(item.TwitterLink).Segments.Last();
            }
        }

        public async Task<TweetQuery?> GetQuoteTweet(string id, TwitterContext twitterContext)
        {
            TweetQuery? tweetResponse = await (from tweet in twitterContext.Tweets
                                               where tweet.Type == TweetType.Lookup &&
                                               tweet.Ids == id &&
                                               tweet.Expansions == $"{ExpansionField.MediaKeys},{ExpansionField.ReferencedTweetAuthorID}" &&
                                               tweet.MediaFields == MediaField.Url &&
                                               tweet.TweetFields == $"{TweetField.CreatedAt}" &&
                                               tweet.UserFields == $"{UserField.UserName}"
                                               select tweet).SingleOrDefaultAsync();
            return tweetResponse;
        }

        public string FixTweetText(string text)
        {
            string fixedText = text;
            if (text.Contains("&amp;"))
            {
                fixedText = fixedText.Replace("&amp;", "&");
            }

            int endLinkPosition = fixedText.LastIndexOf("http");
            if (endLinkPosition != -1)
            {
                fixedText = fixedText.Substring(0, endLinkPosition);
            }
            return fixedText.Trim();
        }

        public string FixTootText(string text)
        {
            HtmlDocument htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(text);
            StringBuilder stringBuilder = new StringBuilder();
            foreach (var node in htmlDoc.DocumentNode.Descendants())
            {
                if (node.NodeType == HtmlNodeType.Text)
                {
                    if (!AnyParentIsElement(node, "a"))
                    {
                        string innerText = node.InnerText;
                        if (innerText.Contains("&amp;"))
                        {
                            innerText = innerText.Replace("&amp;", "&");
                        }
                        stringBuilder.Append(innerText);
                    }
                }
                else if (node.NodeType == HtmlNodeType.Element)
                {
                    if (node.Name == "br")
                    {
                        stringBuilder.Append('\n');
                    }
                    else if (node.Name == "a")
                    {
                        string link = node.GetAttributeValue("href", string.Empty);
                        if (!string.IsNullOrWhiteSpace(link))
                        {
                            stringBuilder.Append(link);
                        }
                    }
                }
            }

            return stringBuilder.ToString().Trim();
        }

        private bool AnyParentIsElement(HtmlNode node, string elementName)
        {
            if (node.ParentNode == null)
            {
                return false;
            }
            if (node.ParentNode.Name == elementName)
            {
                return true;
            }
            return AnyParentIsElement(node.ParentNode, elementName);
        }

        public async Task<MemoryStream?> DownloadImage(string url, HttpClient httpClient)
        {
            MemoryStream stream = new MemoryStream();
            HttpResponseMessage responseMessage = await httpClient.GetAsync(url);
            if (!responseMessage.IsSuccessStatusCode)
            {
                return null;
            }
            await (await responseMessage.Content.ReadAsStreamAsync()).CopyToAsync(stream);
            stream.Position = 0;
            return stream;
        }

        public void DisposePictureStreams(List<Stream> streams)
        {
            foreach (Stream stream in streams)
            {
                stream.Dispose();
            }
        }

        public string GetCarsString(int numberOfCars)
        {
            if (numberOfCars == 1)
            {
                return "car";
            }
            else
            {
                return "cars";
            }
        }
    }
}
