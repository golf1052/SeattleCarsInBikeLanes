using System.Net;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AzureMapsTokenFunction
{
    public class AzureMapsTokenFunction
    {
        private readonly ILogger logger;
        private readonly DefaultAzureCredential credentials;

        public AzureMapsTokenFunction(ILoggerFactory loggerFactory,
            DefaultAzureCredential credentials)
        {
            logger = loggerFactory.CreateLogger<AzureMapsTokenFunction>();
            this.credentials = credentials;
        }

        [Function("AzureMapsToken")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "AzureMaps")] HttpRequestData req)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            var accessToken = await credentials.GetTokenAsync(
                new TokenRequestContext(new[] { "https://atlas.microsoft.com/.default" }));
            response.WriteString(accessToken.Token);
            return response;
        }
    }
}
