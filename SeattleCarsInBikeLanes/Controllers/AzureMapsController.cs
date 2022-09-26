using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AzureMapsController : ControllerBase
    {
        private readonly DefaultAzureCredential credentials;

        public AzureMapsController(DefaultAzureCredential credentials)
        {
            this.credentials = credentials;
        }

        [HttpGet]
        public async Task<string> GetToken()
        {
            var accessToken = await credentials.GetTokenAsync(
                new TokenRequestContext(new[] { "https://atlas.microsoft.com/.default" }));
            return accessToken.Token;
        }
    }
}
