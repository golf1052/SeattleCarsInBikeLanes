using Microsoft.AspNetCore.Mvc;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RedirectController : ControllerBase
    {
        [HttpGet("/redirect")]
        public IActionResult GetTwitter()
        {
            return File("redirect.html", "text/html");
        }

        [HttpGet("/mastodonredirect")]
        public IActionResult GetMastodon()
        {
            return File("mastodonredirect.html", "text/html");
        }

        [HttpGet("/threadsredirect")]
        public IActionResult GetThreads()
        {
            return File("threadsredirect.html", "text/html");
        }
    }
}
