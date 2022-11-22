using golf1052.Mastodon;
using golf1052.Mastodon.Models.Statuses.Media;
using Imgur.API.Endpoints;
using Imgur.API.Models;
using LinqToTwitter;
using LinqToTwitter.Common;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes
{
    public class TwitterToMastodonMigrator
    {
        private readonly ILogger<TwitterToMastodonMigrator> logger;
        private readonly TwitterContext twitterContext;
        private readonly HttpClient httpClient;
        private readonly ReportedItemsDatabase reportedItemsDatabase;
        private readonly HelperMethods helperMethods;
        private readonly ImageEndpoint imgurImageEndpoint;
        private readonly MastodonClientProvider mastodonClientProvider;

        public TwitterToMastodonMigrator(ILogger<TwitterToMastodonMigrator> logger,
            TwitterContext twitterContext,
            HttpClient httpClient,
            ReportedItemsDatabase reportedItemsDatabase,
            HelperMethods helperMethods,
            ImageEndpoint imgurImageEndpoint,
            MastodonClientProvider mastodonClientProvider)
        {
            this.logger = logger;
            this.twitterContext = twitterContext;
            this.httpClient = httpClient;
            this.reportedItemsDatabase = reportedItemsDatabase;
            this.helperMethods = helperMethods;
            this.imgurImageEndpoint = imgurImageEndpoint;
            this.mastodonClientProvider = mastodonClientProvider;
            _ = Run();
        }

        public async Task Run()
        {
            MastodonClient mastodonClient = await mastodonClientProvider.GetClient(new Uri("https://social.ridetrans.it"));
            TweetQuery tweets = await GetAllTweets();
            logger.LogInformation($"Found {tweets.Tweets!.Count} tweets");
            foreach (var tweet in tweets.Tweets!)
            {
                //if (tweet.CreatedAt! < new DateTime(2022, 11, 16, 4, 13, 0, DateTimeKind.Utc))
                //{
                //    // Skipping already posted tweet.
                //    logger.LogInformation($"Skipped tweet because it was already posted. Id {tweet.ID} with text {tweet.Text}");
                //    continue;
                //}

                // First check if the tweet is a reported item tweet
                if (string.IsNullOrWhiteSpace(tweet.Text) || !int.TryParse(new ReadOnlySpan<char>(tweet.Text[0]), out int firstNum))
                {
                    logger.LogInformation($"Skipped tweet because it doesn't look like a reported item. Id {tweet.ID} with text {tweet.Text}");
                    continue;
                }

                // Next find the tweet(s) in the DB
                List<ReportedItem>? reportedItems = await reportedItemsDatabase.GetItems(new List<string>() { tweet.ID! });
                if (reportedItems == null || reportedItems.Count == 0)
                {
                    logger.LogInformation($"Skipped tweet because it couldn't be found in the DB. Id {tweet.ID} with text {tweet.Text}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(tweet.Text))
                {
                    logger.LogInformation($"Skipped tweet because it didn't contain any text. Id {tweet.ID} with text {tweet.Text}");
                    continue;
                }

                string text = FixTweetText(tweet.Text);
                List<Stream> pictureStreams = new List<Stream>();

                // If it's a regular tweet (ie not a quote tweet)
                if (tweet.ReferencedTweets == null)
                {
                    if (tweet.Attachments == null || tweet.Attachments.MediaKeys == null || tweet.Attachments.MediaKeys.Count == 0)
                    {
                        logger.LogInformation($"Skipped tweet because it didn't contain any pictures. Id {tweet.ID} with text {tweet.Text}");
                        continue;
                    }

                    foreach (var mediaKey in tweet.Attachments.MediaKeys)
                    {
                        string? twitterPictureUrl = helperMethods.GetUrlForMediaKey(mediaKey, tweets.Includes!.Media);
                        if (twitterPictureUrl == null)
                        {
                            logger.LogWarning($"Couldn't find media key {mediaKey}. Id {tweet.ID} with text {tweet.Text}");
                            continue;
                        }

                        var stream = await DownloadImage(twitterPictureUrl);
                        if (stream == null)
                        {
                            logger.LogWarning($"Couldn't download picture with media key {mediaKey}. Id {tweet.ID} with text {tweet.Text}");
                            continue;
                        }
                        pictureStreams.Add(stream);
                    }
                }
                // This gets images in quote tweets
                else if (tweet.ReferencedTweets != null)
                {
                    if (tweet.ReferencedTweets.Count == 1)
                    {
                        TweetReference tweetRef = tweet.ReferencedTweets[0];
                        if ("quoted" == tweetRef.Type)
                        {
                            TweetQuery? quotedTweet = await GetQuoteTweet(tweetRef.ID!);
                            if (quotedTweet != null)
                            {
                                if (quotedTweet.Includes != null && quotedTweet.Includes.Media != null)
                                {
                                    tweets.Includes!.Media!.AddRange(quotedTweet.Includes.Media);
                                }

                                Tweet? includesQuotedTweet = tweets.Includes!.Tweets!.FirstOrDefault(t => t.ID == tweetRef.ID);
                                if (includesQuotedTweet != null && includesQuotedTweet.Attachments != null &&
                                    includesQuotedTweet.Attachments.MediaKeys != null)
                                {
                                    foreach (var mediaKey in includesQuotedTweet.Attachments.MediaKeys)
                                    {
                                        string? quotedTweetPictureUrl = helperMethods.GetUrlForMediaKey(mediaKey, tweets.Includes.Media);
                                        if (quotedTweetPictureUrl != null)
                                        {
                                            var stream = await DownloadImage(quotedTweetPictureUrl);
                                            if (stream == null)
                                            {
                                                logger.LogWarning($"Couldn't download picture with media key {mediaKey}. Id {tweet.ID} with text {tweet.Text}");
                                                continue;
                                            }
                                            pictureStreams.Add(stream);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else if (tweet.ReferencedTweets.Count > 1)
                    {
                        logger.LogWarning($"Number of referenced tweets is greater than 1. Id {tweet.ID} with text {tweet.Text}");
                        continue;
                    }
                }

                if (pictureStreams.Count == 0)
                {
                    logger.LogWarning($"No picture streams for tweet. Id {tweet.ID} with text {tweet.Text}");
                    continue;
                }

                if (reportedItems.Count > 1 && pictureStreams.Count != reportedItems.Count)
                {
                    logger.LogWarning($"Skipping transfer of tweet because the number of reported items is greater " +
                        $"than 1 but the number of pictures doesn't match the number of reported items. " +
                        $"Reported items: {reportedItems.Count}. Pictures: {pictureStreams.Count}." +
                        $"Id {tweet.ID} with text {tweet.Text}");
                    DisposePictureStreams(pictureStreams);
                    continue;
                }

                // First upload the pictures to imgur
                List<string> imgurLinks = new List<string>();
                int currentImageCount = 0;
                foreach (var stream in pictureStreams)
                {
                    ReportedItem? reportedItem;

                    if (reportedItems.Count == 1)
                    {
                        reportedItem = reportedItems[0];
                    }
                    else
                    {
                        reportedItem = reportedItems.Where(i => int.Parse(i.TweetId.Split('.')[1]) == currentImageCount).FirstOrDefault();
                        if (reportedItem == null)
                        {
                            logger.LogWarning($"Couldn't find reported item. Id {tweet.ID} with text {tweet.Text}");
                            continue;
                        }
                    }

                    if (reportedItems.Aggregate(0, (acc, cur) => cur.ImgurUrls.Count + acc) == pictureStreams.Count)
                    {
                        logger.LogInformation($"Skipped uploading picture to imgur because it's already there. Id {tweet.ID} with text {tweet.Text}");
                        currentImageCount += 1;
                        continue;
                    }
                    // Create specific imgur stream because imgur upload disposes our stream
                    MemoryStream imgurStream = new MemoryStream();
                    await stream.CopyToAsync(imgurStream);
                    imgurStream.Position = 0;
                    stream.Position = 0;
                    IImage imgurUpload;
                    try
                    {
                        // Note there is a bug in the ImgurAPI package where when a rate limit is hit an exception
                        // will be thrown because the package doesn't process rate limits correctly.
                        imgurUpload = await imgurImageEndpoint.UploadImageAsync(imgurStream);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, $"Failed to upload image to imgur. Image count: {currentImageCount}. Id {tweet.ID} with text {tweet.Text}");
                        continue;
                    }

                    // And save the imgur link to the DB
                    reportedItem.ImgurUrls.Add(imgurUpload.Link);
                    imgurLinks.Add(imgurUpload.Link);
                    bool updatedReportedItemImgurLink = await reportedItemsDatabase.UpdateReportedItem(reportedItem);
                    if (!updatedReportedItemImgurLink)
                    {
                        logger.LogWarning($"Failed to update DB. DB ID {reportedItem.TweetId}. Imgur url: {imgurUpload.Link}");
                    }

                    currentImageCount += 1;
                }

                List<string> attachmentIds = new List<string>();

                // Next upload the images to Mastodon
                foreach (var stream in pictureStreams)
                {
                    try
                    {
                        MastodonAttachment? attachment = await mastodonClient.UploadMedia(stream);
                        attachmentIds.Add(attachment.Id);
                        string attachmentId = attachment.Id;
                        do
                        {
                            attachment = await mastodonClient.GetAttachment(attachmentId);
                            await Task.Delay(500);
                        }
                        while (attachment == null);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, $"Failed to upload image to Mastodon. Imgur links: {string.Join(' ', imgurLinks)} Id {tweet.ID} with text {tweet.Text}");
                        continue;
                    }
                }

                // Finally post the status to Mastodon with the images
                try
                {
                    await mastodonClient.PublishStatus(text, attachmentIds, visibility: "unlisted");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to publish status. Imgur links: {string.Join(' ', imgurLinks)} Attachment ids: {string.Join(' ', attachmentIds)} Id {tweet.ID} with text {tweet.Text}");
                }
                DisposePictureStreams(pictureStreams);
            }
            logger.LogInformation("Done migration!");
        }

        private async Task<TweetQuery?> GetQuoteTweet(string id)
        {
            TweetQuery? tweetResponse = await (from tweet in twitterContext.Tweets
                                       where tweet.Type == TweetType.Lookup &&
                                       tweet.Ids == id &&
                                       tweet.Expansions == $"{ExpansionField.MediaKeys}" &&
                                       tweet.MediaFields == MediaField.Url &&
                                       tweet.TweetFields == $"{TweetField.CreatedAt}"
                                       select tweet).SingleOrDefaultAsync();
            return tweetResponse;
        }

        private async Task<TweetQuery> GetAllTweets()
        {
            string? id = null;
            TweetQuery allTweets = new TweetQuery()
            {
                Tweets = new List<Tweet>(),
                Includes = new TwitterInclude()
                {
                    Tweets = new List<Tweet>(),
                    Media = new List<TwitterMedia>()
                }
            };

            string? nextToken = string.Empty;
            do
            {
                var query = from tweet in twitterContext.Tweets
                            where tweet.Type == TweetType.TweetsTimeline && tweet.ID == HelperMethods.TwitterUserId &&
                            tweet.Expansions == $"{ExpansionField.MediaKeys},{ExpansionField.ReferencedTweetID}" &&
                            tweet.MediaFields == MediaField.Url &&
                            tweet.TweetFields == $"{TweetField.CreatedAt},{TweetField.ReferencedTweets}" &&
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
                    if (response.Includes != null)
                    {
                        if (response.Includes.Tweets != null)
                        {
                            allTweets.Includes.Tweets.AddRange(response.Includes.Tweets);
                        }

                        if (response.Includes.Media != null)
                        {
                            allTweets.Includes.Media.AddRange(response.Includes.Media);
                        }
                    }
                }

                nextToken = response?.Meta?.NextToken;
            }
            while (!string.IsNullOrWhiteSpace(nextToken));

            for (int i = 0; i < allTweets.Tweets.Count; i++)
            {
                Tweet tweet = allTweets.Tweets[i];
                if (tweet.CreatedAt == null)
                {
                    logger.LogWarning($"Removing tweet without created at. Id {tweet.ID} with text {tweet.Text}");
                    allTweets.Tweets.RemoveAt(i);
                    i--;
                }
            }
            allTweets.Tweets.Sort((t1, t2) => t1.CreatedAt!.Value.CompareTo(t2.CreatedAt!.Value));
            return allTweets;
        }

        private async Task<MemoryStream?> DownloadImage(string url)
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

        private string FixTweetText(string text)
        {
            string fixedText = text;
            if (text.Contains("&amp;"))
            {
                fixedText = fixedText.Replace("&amp;", "&");
            }

            int endLinkPosition = fixedText.IndexOf("http");
            if (endLinkPosition != -1)
            {
                fixedText = fixedText.Substring(0, endLinkPosition);
            }
            return fixedText.Trim();
        }

        private void DisposePictureStreams(List<Stream> streams)
        {
            foreach (Stream stream in streams)
            {
                stream.Dispose();
            }
        }
    }
}
