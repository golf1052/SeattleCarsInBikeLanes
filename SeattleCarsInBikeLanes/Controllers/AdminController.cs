using System.Net;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;

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
            KeyVaultSecret adminPasswordSecret = secretClient.GetSecret("admin-password");
            string adminPassword = adminPasswordSecret.Value;
            if (adminPassword != request.Password)
            {
                Response.StatusCode = (int)HttpStatusCode.Unauthorized;
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
    }

    public class DeleteTweetsRequest
    {
        public string Password { get; set; } = string.Empty;
        public List<string> TweetIds { get; set; } = new List<string>();
    }
}
