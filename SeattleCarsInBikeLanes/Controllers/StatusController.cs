using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class StatusController : ControllerBase
    {
        public readonly StatusResponse currentStatus;
        
        public StatusController(StatusResponse currentStatus)
        {
            this.currentStatus = currentStatus;
        }

        [HttpGet]
        public StatusResponse Get()
        {
            return currentStatus;
        }
    }
}
