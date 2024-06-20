using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Mvc;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ThreadsController : ControllerBase
    {
        private readonly HttpClient httpClient;
        private readonly string redirectUri;

        public ThreadsController(IWebHostEnvironment environment,
            HttpClient httpClient,
            SecretClient secretClient)
        {
            this.httpClient = httpClient;

            if (environment.IsDevelopment())
            {
                redirectUri = "https://localhost:7152/threadsredirect";
            }
            else
            {
                redirectUri = "https://seattle.carinbikelane.com/threadsredirect";
            }
        }
    }
}
