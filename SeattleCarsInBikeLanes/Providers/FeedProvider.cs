using System.ServiceModel.Syndication;
using System.Text;
using System.Xml;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;

namespace SeattleCarsInBikeLanes.Providers
{
    public class FeedProvider
    {
        public const string RssContentType = "application/rss+xml";
        public const string AtomContentType = "application/atom+xml";

        private const string RssFilename = "rss.xml";
        private const string AtomFilename = "atom.xml";

        private readonly ILogger<FeedProvider> logger;
        private readonly ReportedItemsDatabase reportedItemsDatabase;
        private readonly BlobContainerClient blobContainerClient;

        public FeedProvider(ILogger<FeedProvider> logger,
            ReportedItemsDatabase reportedItemsDatabase,
            BlobContainerClient blobContainerClient)
        {
            this.logger = logger;
            this.reportedItemsDatabase = reportedItemsDatabase;
            this.blobContainerClient = blobContainerClient;
        }

        public async Task<string> GetRssFeed()
        {
            BlobClient rssFileBlob = blobContainerClient.GetBlobClient(RssFilename);
            var download = await rssFileBlob.DownloadContentAsync();
            return download.Value.Content.ToString();
        }

        public async Task<string> GetAtomFeed()
        {
            BlobClient atomFileBlob = blobContainerClient.GetBlobClient(AtomFilename);
            var download = await atomFileBlob.DownloadContentAsync();
            return download.Value.Content.ToString();
        }

        public async Task AddReportedItemToFeed(ReportedItem reportedItem)
        {
            BlobClient rssFileBlob = blobContainerClient.GetBlobClient(RssFilename);
            BlobClient atomFileBlob = blobContainerClient.GetBlobClient(AtomFilename);
            var rssDownload = await rssFileBlob.DownloadContentAsync();
            var atomDownload = await atomFileBlob.DownloadContentAsync();

            using XmlReader rssXmlReader = XmlReader.Create(rssDownload.Value.Content.ToStream());
            Rss20FeedFormatter rssFormatter = new Rss20FeedFormatter();
            rssFormatter.ReadFrom(rssXmlReader);
            SyndicationFeed rssFeed = rssFormatter.Feed;
            List<SyndicationItem> rssItems = rssFeed.Items.ToList();
            AddReportedItemToFeed(reportedItem, rssItems, true);
            rssFeed.Items = rssItems;

            using XmlReader atomXmlReader = XmlReader.Create(atomDownload.Value.Content.ToStream());
            Atom10FeedFormatter atomFormatter = new Atom10FeedFormatter();
            atomFormatter.ReadFrom(atomXmlReader);
            SyndicationFeed atomFeed = atomFormatter.Feed;
            List<SyndicationItem> atomItems = atomFeed.Items.ToList();
            AddReportedItemToFeed(reportedItem, atomItems, true);
            atomFeed.Items = atomItems;

            using MemoryStream rssMemoryStream = WriteRssFeed(rssFeed);
            using MemoryStream atomMemoryStream = WriteAtomFeed(atomFeed);

            await rssFileBlob.UploadAsync(rssMemoryStream, new BlobHttpHeaders() { ContentType = RssContentType });
            await atomFileBlob.UploadAsync(atomMemoryStream, new BlobHttpHeaders() { ContentType = AtomContentType });
        }

        public async Task RemoveReportedItemFromFeed(ReportedItem reportedItem)
        {
            BlobClient rssFileBlob = blobContainerClient.GetBlobClient(RssFilename);
            BlobClient atomFileBlob = blobContainerClient.GetBlobClient(AtomFilename);
            var rssDownload = await rssFileBlob.DownloadContentAsync();
            var atomDownload = await atomFileBlob.DownloadContentAsync();

            using XmlReader rssXmlReader = XmlReader.Create(rssDownload.Value.Content.ToStream());
            Rss20FeedFormatter rssFormatter = new Rss20FeedFormatter();
            rssFormatter.ReadFrom(rssXmlReader);
            SyndicationFeed rssFeed = rssFormatter.Feed;
            List<SyndicationItem> rssItems = rssFeed.Items.ToList();
            RemoveReportedItemFromFeed(reportedItem, rssItems);
            rssFeed.Items = rssItems;

            using XmlReader atomXmlReader = XmlReader.Create(atomDownload.Value.Content.ToStream());
            Atom10FeedFormatter atomFormatter = new Atom10FeedFormatter();
            atomFormatter.ReadFrom(atomXmlReader);
            SyndicationFeed atomFeed = atomFormatter.Feed;
            List<SyndicationItem> atomItems = atomFeed.Items.ToList();
            RemoveReportedItemFromFeed(reportedItem, atomItems);
            atomFeed.Items = atomItems;

            using MemoryStream rssMemoryStream = WriteRssFeed(rssFeed);
            using MemoryStream atomMemoryStream = WriteAtomFeed(atomFeed);

            await rssFileBlob.UploadAsync(rssMemoryStream, new BlobHttpHeaders() { ContentType = RssContentType });
            await atomFileBlob.UploadAsync(atomMemoryStream, new BlobHttpHeaders() { ContentType = AtomContentType });
        }

        public async Task RebuildFeed()
        {
            BlobClient rssFileBlob = blobContainerClient.GetBlobClient(RssFilename);
            BlobClient atomFileBlob = blobContainerClient.GetBlobClient(AtomFilename);
            await rssFileBlob.DeleteIfExistsAsync();
            await atomFileBlob.DeleteIfExistsAsync();

            List<ReportedItem>? reportedItems = await reportedItemsDatabase.GetLatestItems(100);
            if (reportedItems == null)
            {
                throw new Exception("Couldn't rebuild feed. No reported items found.");
            }

            SyndicationFeed feed = new SyndicationFeed("Cars in Bike Lanes Seattle", "Pictures of cars in bike lanes. Accepting submissions through website, Twitter/Mastodon mention, or DM.", new Uri("https://seattle.carinbikelane.com"));
            SyndicationPerson author = new SyndicationPerson("seattlecarsinbikelanes@outlook.com", "Cars in Bike Lanes Seattle", "https://seattle.carinbikelane.com");
            feed.Authors.Add(author);

            List<SyndicationItem> feedItems = new List<SyndicationItem>();

            DateTimeOffset lastUpdatedTime = DateTimeOffset.UtcNow;
            if (reportedItems.Count > 0)
            {
                lastUpdatedTime = new DateTimeOffset(reportedItems[0].CreatedAt);
            }

            foreach (var reportedItem in reportedItems)
            {
                AddReportedItemToFeed(reportedItem, feedItems, false);
            }

            feed.Items = feedItems;
            feed.Language = "en-us";
            feed.LastUpdatedTime = lastUpdatedTime;

            using MemoryStream rssMemoryStream = WriteRssFeed(feed);
            using MemoryStream atomMemoryStream = WriteAtomFeed(feed);

            await rssFileBlob.UploadAsync(rssMemoryStream, new BlobHttpHeaders() { ContentType = RssContentType });
            await atomFileBlob.UploadAsync(atomMemoryStream, new BlobHttpHeaders() { ContentType = AtomContentType });
        }

        private MemoryStream WriteRssFeed(SyndicationFeed feed)
        {
            MemoryStream rssMemoryStream = new MemoryStream();
            StreamWriter rssWriter = new StreamWriter(rssMemoryStream, Encoding.UTF8);
            using XmlWriter rssXmlWriter = XmlWriter.Create(rssWriter);
            Rss20FeedFormatter rssFormatter = new Rss20FeedFormatter(feed);
            rssFormatter.WriteTo(rssXmlWriter);
            rssXmlWriter.Flush();
            rssWriter.Flush();
            rssXmlWriter.Close();
            rssMemoryStream.Position = 0;
            return rssMemoryStream;
        }

        private MemoryStream WriteAtomFeed(SyndicationFeed feed)
        {
            MemoryStream atomMemoryStream = new MemoryStream();
            StreamWriter atomWriter = new StreamWriter(atomMemoryStream, Encoding.UTF8);
            using XmlWriter atomXmlWriter = XmlWriter.Create(atomWriter);
            Atom10FeedFormatter atomFormatter = new Atom10FeedFormatter(feed);
            atomFormatter.WriteTo(atomXmlWriter);
            atomXmlWriter.Flush();
            atomWriter.Flush();
            atomXmlWriter.Close();
            atomMemoryStream.Position = 0;
            return atomMemoryStream;
        }

        private void AddReportedItemToFeed(ReportedItem reportedItem, List<SyndicationItem> feedItems, bool beginning)
        {
            StringBuilder contentBuilder = new StringBuilder($"<p>{reportedItem.NumberOfCars} ");
            StringBuilder titleBuilder = new StringBuilder($"{reportedItem.NumberOfCars} ");
            if (reportedItem.NumberOfCars == 1)
            {
                contentBuilder.Append("car<br />");
                titleBuilder.Append("car");
            }
            else
            {
                contentBuilder.Append("cars<br />");
                titleBuilder.Append("cars");
            }

            if (reportedItem.Date != null)
            {
                contentBuilder.Append($"Date: {reportedItem.Date.Value.ToString("M/d/yyyy")}<br />");
            }

            if (reportedItem.Time != null)
            {
                contentBuilder.Append($"Time: {reportedItem.Time.Value.ToString("h:mm tt")}<br />");
            }

            string fixedLocationString = reportedItem.LocationString.Replace("&amp;", "and").Replace("&", "and");
            contentBuilder.Append($"Location: {fixedLocationString}<br />");
            titleBuilder.Append($" @ {fixedLocationString}");

            if (reportedItem.Location != null)
            {
                contentBuilder.Append($"GPS: {reportedItem.Location.Position.Latitude}, {reportedItem.Location.Position.Longitude}<br /></p>");
            }
            else
            {
                contentBuilder.Append("</p>");
            }

            if (reportedItem.ImgurUrls != null && reportedItem.ImgurUrls.Count > 0)
            {
                foreach (var imgurLink in reportedItem.ImgurUrls)
                {
                    contentBuilder.Append($"<img src=\"{imgurLink}\" style=\"width: 300px;\" />");
                }
            }
            else if (reportedItem.ImageUrls != null && reportedItem.ImageUrls.Count > 0)
            {
                foreach (var imageLink in reportedItem.ImageUrls)
                {
                    contentBuilder.Append($"<img src=\"{imageLink}\" style=\"width: 300px;\" />");
                }
            }

            if (!string.IsNullOrWhiteSpace(reportedItem.TwitterLink))
            {
                contentBuilder.Append($"<p><a href=\"{reportedItem.TwitterLink}\">Twitter post</a></p>");
            }
            else if (reportedItem.TweetId.Length < 36)
            {
                string tweetId = reportedItem.TweetId.Split('.')[0];
                contentBuilder.Append($"<p><a href=\"https://twitter.com/carbikelanesea/status/{tweetId}\">Twitter post</a></p>");
            }

            if (!string.IsNullOrWhiteSpace(reportedItem.MastodonLink))
            {
                contentBuilder.Append($"<p><a href=\"{reportedItem.MastodonLink}\">Mastodon post</a></p>");
            }

            if (!string.IsNullOrWhiteSpace(reportedItem.BlueskyLink))
            {
                contentBuilder.Append($"<p><a href=\"{reportedItem.BlueskyLink}\">Bluesky post</a></p>");
            }

            TextSyndicationContent content = SyndicationContent.CreateHtmlContent(contentBuilder.ToString());
            DateTimeOffset pubDate = new DateTimeOffset(reportedItem.CreatedAt);
            SyndicationItem item = new SyndicationItem(titleBuilder.ToString(), content, null, reportedItem.TweetId, pubDate);
            item.PublishDate = pubDate;

            if (feedItems.Count >= 100)
            {
                feedItems.RemoveAt(feedItems.Count - 1);
            }

            if (beginning)
            {
                feedItems.Insert(0, item);
            }
            else
            {
                feedItems.Add(item);
            }
        }

        private void RemoveReportedItemFromFeed(ReportedItem reportedItem, List<SyndicationItem> feedItems)
        {
            for (int i = 0; i < feedItems.Count; i++)
            {
                if (feedItems[i].Id == reportedItem.TweetId)
                {
                    feedItems.RemoveAt(i);
                    return;
                }
            }

            logger.LogWarning($"Did not find reported item to remove. Id {reportedItem.TweetId}");
        }
    }
}
