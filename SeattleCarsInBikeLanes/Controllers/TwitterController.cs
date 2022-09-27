using LinqToTwitter;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TwitterController : ControllerBase
    {
        private const string TwitterUsername = "carbikelanesea";

        private readonly TwitterContext twitterContext;

        public TwitterController(TwitterContext twitterContext)
        {
            this.twitterContext = twitterContext;
        }

        [HttpGet("oembed")]
        public async Task<string?> GetOEmbed([FromQuery] string tweetId)
        {
            string url = $"https://twitter.com/{TwitterUsername}/status/{tweetId}";
            HttpClient client = new HttpClient();
            HttpResponseMessage response = await client.GetAsync($"https://publish.twitter.com/oembed?url={url}&dnt=true");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
    }
}
