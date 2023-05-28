using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class SyndicationController : ControllerBase
    {
        private readonly FeedProvider feedProvider;

        public SyndicationController(FeedProvider feedProvider)
        {
            this.feedProvider = feedProvider;
        }

        [HttpHead("/rss")]
        [HttpHead("/rss.xml")]
        public async Task<IActionResult> GetRssFeedHead()
        {
            string feed = await feedProvider.GetRssFeed();
            Response.ContentType = FeedProvider.RssContentType;
            Response.ContentLength = feed.Length + 2;
            return Ok();
        }

        [HttpGet("/rss")]
        [HttpGet("/rss.xml")]
        public async Task<IActionResult> GetRssFeed()
        {
            return Content(await feedProvider.GetRssFeed(), FeedProvider.RssContentType);
        }

        [HttpHead("/atom")]
        [HttpHead("/atom.xml")]
        public async Task<IActionResult> GetAtomFeedHead()
        {
            string feed = await feedProvider.GetAtomFeed();
            Response.ContentType = FeedProvider.AtomContentType;
            Response.ContentLength = feed.Length + 2;
            return Ok();
        }

        [HttpGet("/atom")]
        [HttpGet("/atom.xml")]
        public async Task<IActionResult> GetAtomFeed()
        {
            return Content(await feedProvider.GetAtomFeed(), FeedProvider.AtomContentType);
        }
    }
}
