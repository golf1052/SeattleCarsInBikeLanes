using Microsoft.AspNetCore.Mvc;

namespace SeattleCarsInBikeLanes.Controllers
{
    [ApiController]
    public class PrivacyController : ControllerBase
    {
        [HttpGet("/privacy")]
        public IActionResult Get()
        {
            return File("privacy.html", "text/html");
        }
    }
}
