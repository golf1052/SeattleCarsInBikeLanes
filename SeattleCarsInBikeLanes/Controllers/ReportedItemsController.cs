using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ReportedItemsController : ControllerBase
    {
        private readonly ILogger<ReportedItemsController> logger;
        private readonly ReportedItemsDatabase reportedItemsDatabase;

        public ReportedItemsController(ILogger<ReportedItemsController> logger,
            ReportedItemsDatabase reportedItemsDatabase)
        {
            this.logger = logger;
            this.reportedItemsDatabase = reportedItemsDatabase;
        }

        [HttpGet("search")]
        public async Task<List<ReportedItem>> SearchItems([FromQuery] ReportedItemsSearchRequest request)
        {
            var items = await reportedItemsDatabase.SearchItems(request);
            if (items == null)
            {
                return new List<ReportedItem>();
            }
            else
            {
                return items;
            }
        }

        [HttpGet("all")]
        public async Task<List<ReportedItem>> GetAllItems()
        {
            var items = await reportedItemsDatabase.GetAllItems();
            if (items == null)
            {
                return new List<ReportedItem>();
            }
            else
            {
                return items;
            }
        }
    }
}
