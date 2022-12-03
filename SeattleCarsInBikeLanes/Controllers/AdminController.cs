using System.ComponentModel.DataAnnotations;
using System.Net;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Models.TypeConverters;

namespace SeattleCarsInBikeLanes.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AdminController : ControllerBase
    {
        private readonly ILogger<AdminController> logger;
        private readonly SecretClient secretClient;
        private readonly ReportedItemsDatabase reportedItemsDatabase;
        private readonly HelperMethods helperMethods;

        public AdminController(ILogger<AdminController> logger,
            SecretClient secretClient,
            ReportedItemsDatabase reportedItemsDatabase,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.secretClient = secretClient;
            this.reportedItemsDatabase = reportedItemsDatabase;
            this.helperMethods = helperMethods;
        }

        [HttpPatch("UpdateLocation")]
        public async Task<string> UpdateLocation([FromBody] UpdateReportedItemLocationRequest request)
        {
            if (!helperMethods.IsAuthorized(request, secretClient))
            {
                Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return string.Empty;
            }

            Position? newLocation = new PositionConverter().ConvertFrom(null, null, request.NewLocation) as Position;
            if (newLocation == null || (newLocation.Latitude == 0 && newLocation.Longitude == 0))
            {
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return "Position should not be set to Null Island";
            }

            logger.LogInformation($"Starting location update of tweet {request.TweetId}");
            string returnString;
            ReportedItem? item = await reportedItemsDatabase.GetItem(request.TweetId);
            if (item == null)
            {
                returnString = "0 tweets to update found.";
                logger.LogInformation(returnString);
                return returnString;
            }

            returnString = $"Old location: {item.Location?.Position.Latitude}, {item.Location?.Position.Longitude}.";
            item.Location = new Point(newLocation);
            bool updated = await reportedItemsDatabase.UpdateReportedItem(item);
            if (!updated)
            {
                logger.LogWarning($"Failed to update {request.TweetId}.");
                return $"Failed to update {request.TweetId}. {returnString}";
            }
            else
            {
                returnString += $" New Location: {item.Location.Position.Latitude}, {item.Location.Position.Longitude}.";
                logger.LogInformation(returnString);
                return returnString;
            }
        }
    }

    public class UpdateReportedItemLocationRequest : AdminRequest
    {
        public string Password { get; set; } = string.Empty;

        [Required]
        public string TweetId { get; set; } = string.Empty;

        [Required]
        public string NewLocation { get; set; } = string.Empty;
    }
}
