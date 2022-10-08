using System.ComponentModel.DataAnnotations;
using System.Net;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
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

        public AdminController(ILogger<AdminController> logger,
            SecretClient secretClient,
            ReportedItemsDatabase reportedItemsDatabase)
        {
            this.logger = logger;
            this.secretClient = secretClient;
            this.reportedItemsDatabase = reportedItemsDatabase;
        }

        [HttpDelete("DeleteTweets")]
        public async Task<string> DeleteTweets([FromBody] DeleteTweetsRequest request)
        {
            if (!IsAuthorized(request))
            {
                return string.Empty;
            }

            logger.LogInformation($"Starting deletion of tweet ids: {string.Join(", ", request.TweetIds)}");
            string returnString;
            List<ReportedItem>? items = await reportedItemsDatabase.GetItems(request.TweetIds);
            if (items == null || items.Count == 0)
            {
                returnString = "0 tweets to delete found.";
                logger.LogInformation(returnString);
                return returnString;
            }

            int tweetsDeleted = 0;
            bool deletedLatest = false;
            foreach (var item in items)
            {
                bool deleted = await reportedItemsDatabase.DeleteItem(item);
                if (deleted)
                {
                    tweetsDeleted += 1;
                    if (item.Latest)
                    {
                        deletedLatest = true;
                    }
                }
            }

            if (deletedLatest)
            {
                logger.LogInformation("Deleted latest tweet. Marking new latest tweet.");
                var allItems = await reportedItemsDatabase.GetAllItems();
                if (allItems == null)
                {
                    logger.LogWarning("Could not get all items and therefore could not mark latest tweet.");
                }
                else
                {
                    if (allItems.Count > 0)
                    {
                        allItems = allItems.OrderByDescending(i => i.CreatedAt).ToList();
                        allItems[0].Latest = true;
                        await reportedItemsDatabase.UpdateReportedItem(allItems[0]);
                    }
                }
            }

            returnString = $"{request.TweetIds.Count} tweets requested. {tweetsDeleted} tweets deleted.";
            logger.LogInformation(returnString);
            return returnString;
        }

        [HttpPatch("UpdateLocation")]
        public async Task<string> UpdateLocation([FromBody] UpdateReportedItemLocationRequest request)
        {
            if (!IsAuthorized(request))
            {
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

        private bool IsAuthorized(AdminRequest request)
        {
            KeyVaultSecret adminPasswordSecret = secretClient.GetSecret("admin-password");
            string adminPassword = adminPasswordSecret.Value;
            if (adminPassword != request.Password)
            {
                Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return false;
            }
            else
            {
                return true;
            }
        }
    }

    public interface AdminRequest
    {
        public string Password { get; set; }
    }

    public class DeleteTweetsRequest : AdminRequest
    {
        public string Password { get; set; } = string.Empty;
        public List<string> TweetIds { get; set; } = new List<string>();
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
