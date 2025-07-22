using golf1052.atproto.net.Models.Bsky.OEmbed;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BlueskyController : ControllerBase
    {
        private readonly ILogger<BlueskyController> logger;
        private readonly HttpClient httpClient;

        public BlueskyController(ILogger<BlueskyController> logger, HttpClient httpClient)
        {
            this.logger = logger;
            this.httpClient = httpClient;
        }

        [HttpGet("oembed")]
        public async Task<string?> GetOEmbed([FromQuery] string url, [FromQuery] int? width, [FromQuery] int? height)
        {
            // Build the oEmbed URL with optional width and height parameters
            string oembedUrl = $"https://embed.bsky.app/oembed?url={Uri.EscapeDataString(url)}&format=json";

            if (width.HasValue)
            {
                oembedUrl += $"&maxwidth={width.Value}";
            }

            if (height.HasValue)
            {
                oembedUrl += $"&maxheight={height.Value}";
            }

            HttpResponseMessage response = await httpClient.GetAsync(oembedUrl);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to fetch Bluesky oEmbed for URL: {Url}, Status: {StatusCode}", url, response.StatusCode);
                return null;
            }

            string jsonResponse = await response.Content.ReadAsStringAsync();
            BlueskyOEmbed embed = JsonConvert.DeserializeObject<BlueskyOEmbed>(jsonResponse)!;
            return embed.Html;
        }
    }
}
