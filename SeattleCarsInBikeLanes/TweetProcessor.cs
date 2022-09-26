using Azure.Maps.Search;
using LinqToTwitter;
using LinqToTwitter.Common;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using static System.Net.Mime.MediaTypeNames;

namespace SeattleCarsInBikeLanes
{
    public class TweetProcessor
    {
        private readonly ILogger<TweetProcessor> logger;
        private readonly TwitterContext twitterContext;
        private readonly MapsSearchClient mapsSearchClient;
        private readonly ReportedItemsDatabase reportedItemsDatabase;
        private readonly TimeSpan checkDuration;
        private readonly HelperMethods helperMethods;
        private Task checkTask;

        public TweetProcessor(ILogger<TweetProcessor> logger,
            TwitterContext twitterContext,
            MapsSearchClient mapsSearchClient,
            ReportedItemsDatabase reportedItemsDatabase,
            TimeSpan checkDuration,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.twitterContext = twitterContext;
            this.mapsSearchClient = mapsSearchClient;
            this.reportedItemsDatabase = reportedItemsDatabase;
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
                    logger.LogWarning("More than 1 item was probably marked as latest. Fixing.");
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
                latestItem.Latest = false;
                bool updatedLatest = await reportedItemsDatabase.UpdateReportedItem(latestItem);
                if (!updatedLatest)
                {
                    logger.LogError($"Failed to update latest tweet");
                }
                await ImportTweetsToDatabase(tweets);
                logger.LogInformation("Imported latest tweets");
            }
        }

        private async Task ImportAllTweetsToDatabase()
        {
            var tweets = await GetAllTweets();
            await ImportTweetsToDatabase(tweets);
            logger.LogInformation("Imported all tweets.");
        }

        private async Task ImportTweetsToDatabase(TweetQuery tweets)
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

            allItems = allItems.OrderByDescending(i => i.CreatedAt).ToList();
            if (allItems.Count > 0)
            {
                allItems[0].Latest = true;
                foreach (var item in allItems)
                {
                    bool added = await reportedItemsDatabase.AddReportedItem(item);
                    if (!added)
                    {
                        logger.LogError($"Failed to add tweet: {item.TweetId}");
                    }
                }
            }
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
