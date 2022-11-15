using Microsoft.AspNetCore.Mvc;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RedirectController : ControllerBase
    {
        [HttpGet("/redirect")]
        public IActionResult Get()
        {
            return File("redirect.html", "text/html");
        }
    }
}
