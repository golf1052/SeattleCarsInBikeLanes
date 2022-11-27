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

        [HttpGet("rss")]
        public async Task<IActionResult> GetRssFeed()
        {
            return Content(await feedProvider.GetRssFeed(), FeedProvider.RssContentType);
        }

        [HttpGet("rss.xml")]
        public async Task<IActionResult> GetRssXmlFeed()
        {
            return Content(await feedProvider.GetRssFeed(), FeedProvider.RssContentType);
        }

        [HttpGet("atom")]
        public async Task<IActionResult> GetAtomFeed()
        {
            return Content(await feedProvider.GetAtomFeed(), FeedProvider.AtomContentType);
        }

        [HttpGet("atom.xml")]
        public async Task<IActionResult> GetAtomXmlFeed()
        {
            return Content(await feedProvider.GetAtomFeed(), FeedProvider.AtomContentType);
        }
    }
}
