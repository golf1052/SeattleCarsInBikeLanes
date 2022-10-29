using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace TwitterFunction
{
    public class GetOEmbedFunction
    {
        private const string TwitterUsername = "carbikelanesea";

        private readonly ILogger logger;

        public GetOEmbedFunction(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<GetOEmbedFunction>();
        }

        [Function("GetOEmbed")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "twitter/oembed")] HttpRequestData req,
            FunctionContext executionContext)
        {
            var queryCollection = req.Url.ParseQueryString();
            string? tweetId = queryCollection.Get("tweetId");
            HttpResponseData res;
            if (tweetId == null)
            {
                res = req.CreateResponse(HttpStatusCode.BadRequest);
                res.WriteString("tweetId is null");
                return res;
            }

            string url = $"https://twitter.com/{TwitterUsername}/status/{tweetId}";
            HttpClient client = new HttpClient();
            HttpResponseMessage response = await client.GetAsync($"https://publish.twitter.com/oembed?url={url}&dnt=true");
            if (!response.IsSuccessStatusCode)
            {
                return req.CreateResponse(HttpStatusCode.InternalServerError);
            }

            res = req.CreateResponse(HttpStatusCode.OK);
            res.WriteString(await response.Content.ReadAsStringAsync());

            return res;
        }
    }
}
