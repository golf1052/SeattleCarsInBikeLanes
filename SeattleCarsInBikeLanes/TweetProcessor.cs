using Azure.Maps.Search;
using LinqToTwitter;
using LinqToTwitter.Common;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;
using static System.Net.Mime.MediaTypeNames;

namespace SeattleCarsInBikeLanes
{
    public class TweetProcessor
    {
        private readonly ILogger<TweetProcessor> logger;
        private readonly TwitterContext twitterContext;
        private readonly MapsSearchClient mapsSearchClient;
        private readonly ReportedItemsDatabase reportedItemsDatabase;
        private readonly StatusResponse currentStatus;
        private readonly TimeSpan checkDuration;
        private readonly HelperMethods helperMethods;
        private Task checkTask;

        public TweetProcessor(ILogger<TweetProcessor> logger,
            TwitterContext twitterContext,
            MapsSearchClient mapsSearchClient,
            ReportedItemsDatabase reportedItemsDatabase,
            StatusResponse currentStatus,
            TimeSpan checkDuration,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.twitterContext = twitterContext;
            this.mapsSearchClient = mapsSearchClient;
            this.reportedItemsDatabase = reportedItemsDatabase;
            this.currentStatus = currentStatus;
            this.checkDuration = checkDuration;
            this.helperMethods = helperMethods;
            checkTask = CheckTweets();
            _ = CheckCheckTweets();
        }

        private async Task CheckTweets()
        {
            while (true)
            {
                await ImportLatestTweetsToDatabase();
                await Task.Delay(checkDuration);
            }
        }

        private async Task ImportLatestTweetsToDatabase()
        {
            ReportedItem? latestItem = await reportedItemsDatabase.GetLatestReportedItem();
            if (latestItem == null)
            {
                List<ReportedItem>? allItems = await reportedItemsDatabase.GetAllItems();
                if (allItems == null)
                {
                    logger.LogError("Failed to get all items from database. Not importing tweets.");
                    return;
                }
                else if (allItems.Count == 0)
                {
                    logger.LogWarning("No items in database. Importing all tweets.");
                    await ImportAllTweetsToDatabase();
                    return;
                }
                else
                {
                    logger.LogWarning("Could not find latest item. Fixing.");
                    allItems = allItems.OrderByDescending(i => i.CreatedAt).ToList();
                    foreach (var item in allItems)
                    {
                        item.Latest = false;
                    }
                    allItems[0].Latest = true;

                    foreach (var item in allItems)
                    {
                        bool updated = await reportedItemsDatabase.UpdateReportedItem(item);
                        if (!updated)
                        {
                            logger.LogError($"Failed to update tweet: {item.TweetId}");
                        }
                    }

                    latestItem = allItems[0];
                }
            }

            var tweets = await GetAllTweetsSinceId(helperMethods.GetRealTweetId(latestItem));
            if (tweets != null && tweets.Tweets != null && tweets.Tweets.Count > 0)
            {
                List<ReportedItem> reportedItems = await GetReportedItems(tweets);
                if (reportedItems.Count > 0)
                {
                    latestItem.Latest = false;
                    bool updatedLatest = await reportedItemsDatabase.UpdateReportedItem(latestItem);
                    if (!updatedLatest)
                    {
                        logger.LogError($"Failed to update latest tweet");
                    }
                    await ImportTweetsToDatabase(reportedItems);
                    logger.LogInformation("Imported latest tweets");
                    latestItem = reportedItems[0];
                }
            }

            currentStatus.LatestTweet = latestItem.CreatedAt;
            currentStatus.LastChecked = DateTime.UtcNow;
        }

        private async Task ImportAllTweetsToDatabase()
        {
            var tweets = await GetAllTweets();
            List<ReportedItem> reportedItems = await GetReportedItems(tweets);
            await ImportTweetsToDatabase(reportedItems);
            logger.LogInformation("Imported all tweets.");
        }

        private async Task RebuildDatabase()
        {
            bool deletedAll = await reportedItemsDatabase.DeleteAllItems();
            if (!deletedAll)
            {
                logger.LogError("Not all items were deleted. Please delete the rest manually");
                return;
            }
            logger.LogInformation("Deleted all reported items.");

            await ImportAllTweetsToDatabase();
        }

        private async Task ImportTweetsToDatabase(List<ReportedItem> reportedItems)
        {
            if (reportedItems.Count > 0)
            {
                reportedItems[0].Latest = true;
                foreach (var item in reportedItems)
                {
                    bool added = await reportedItemsDatabase.AddReportedItem(item);
                    if (!added)
                    {
                        logger.LogError($"Failed to add tweet: {item.TweetId}");
                    }
                }
            }
        }

        private async Task<List<ReportedItem>> GetReportedItems(TweetQuery tweets)
        {
            List<ReportedItem> allItems = new List<ReportedItem>();
            foreach (var tweet in tweets.Tweets!)
            {
                List<ReportedItem>? reportedItems = await helperMethods.TweetToReportedItem(tweet,
                    tweets.Includes?.Media,
                    mapsSearchClient);

                if (reportedItems == null)
                {
                    continue;
                }

                foreach (var reportedItem in reportedItems)
                {
                    allItems.Add(reportedItem);

                }
            }
            return allItems.OrderByDescending(i => i.CreatedAt).ToList();
        }

        private async Task<TweetQuery> GetAllTweets()
        {
            return await GetAllTweetsSinceId(null);
        }

        private async Task<TweetQuery> GetAllTweetsSinceId(string? id)
        {
            TweetQuery allTweets = new TweetQuery()
            {
                Tweets = new List<Tweet>(),
                Includes = new TwitterInclude()
                {
                    Media = new List<TwitterMedia>()
                }
            };

            string? nextToken = string.Empty;
            do
            {
                var query = from tweet in twitterContext.Tweets
                            where tweet.Type == TweetType.TweetsTimeline && tweet.ID == HelperMethods.TwitterUserId &&
                            tweet.Exclude == TweetExcludes.Replies &&
                            tweet.Expansions == ExpansionField.MediaKeys &&
                            tweet.MediaFields == MediaField.Url &&
                            tweet.TweetFields == TweetField.CreatedAt &&
                            tweet.MaxResults == 100 &&
                            tweet.PaginationToken == nextToken
                            select tweet;

                if (!string.IsNullOrEmpty(id))
                {
                    query = from tweet in query
                            where tweet.SinceID == id
                            select tweet;
                }

                var response = await query.SingleOrDefaultAsync();

                if (response != null && response.Tweets != null)
                {
                    allTweets.Tweets.AddRange(response.Tweets);
                    if (response.Includes != null && response.Includes.Media != null)
                    {
                        allTweets.Includes.Media.AddRange(response.Includes.Media);
                    }
                }

                nextToken = response?.Meta?.NextToken;
            }
            while (!string.IsNullOrWhiteSpace(nextToken));

            return allTweets;
        }

        private async Task CheckCheckTweets()
        {
            while (true)
            {
                if (checkTask.IsFaulted)
                {
                    logger.LogError("Check tweets task has failed, restarting.");
                    if (checkTask.Exception != null)
                    {
                        foreach (var exception in checkTask.Exception.InnerExceptions)
                        {
                            logger.LogError(exception, "InnerException");
                        }
                    }
                    checkTask = CheckTweets();
                }
                await Task.Delay((int)checkDuration.TotalMilliseconds / 2);
            }
        }
    }
}
