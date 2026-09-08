using Azure.Identity;
using idunno.Authentication.Basic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Controllers
{
    [ApiController]
    [Route("api/AdminPage/BlockedDevices")]
    [Authorize(AuthenticationSchemes = BasicAuthenticationDefaults.AuthenticationScheme)]
    public class AdminDeviceBlocksController : ControllerBase
    {
        private readonly ILogger<AdminDeviceBlocksController> logger;
        private readonly BlockedDevicesDatabase database;

        public AdminDeviceBlocksController(ILogger<AdminDeviceBlocksController> logger, BlockedDevicesDatabase database)
        {
            this.logger = logger;
            this.database = database;
        }

        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Get(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var devices = await database.ListAsync(cancellationToken);
                return Ok(devices.Select(device => new AdminBlockedDeviceResponse(device.DeviceId, device.Reason)).ToArray());
            }
            catch (Exception ex) when (IsStorageFailure(ex, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogError(ex, "Could not load blocked devices.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    "Blocked devices could not be loaded. Refresh the blocked-device list and try again. If this continues, check the storage configuration.");
            }
        }

        [HttpPost]
        [Consumes("application/json")]
        public async Task<IActionResult> Block([FromBody] BlockDeviceRequest request, CancellationToken cancellationToken = default)
        {
            string? deviceId = DeviceIdRules.Normalize(request.DeviceId);
            if (!DeviceIdRules.IsValid(deviceId))
            {
                return BadRequest("A device ID of 1-128 ASCII letters, digits, hyphens or underscores is required.");
            }

            string? reason = request.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > BlockedDevicesDatabase.MaximumReasonLength)
            {
                return BadRequest($"A reason of 1-{BlockedDevicesDatabase.MaximumReasonLength} characters is required.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await database.BlockAsync(deviceId!, reason, cancellationToken);
                return NoContent();
            }
            catch (Exception ex) when (IsStorageFailure(ex, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogError(ex, "Could not confirm blocking device {DeviceId}.", deviceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    "The device block could not be confirmed. Refresh the blocked-device list before retrying. If this continues, check the storage configuration.");
            }
        }

        [HttpDelete]
        [Consumes("application/json")]
        public async Task<IActionResult> Unblock([FromBody] UnblockDeviceRequest request, CancellationToken cancellationToken = default)
        {
            string? deviceId = DeviceIdRules.Normalize(request.DeviceId);
            if (!DeviceIdRules.IsValid(deviceId))
            {
                return BadRequest("A device ID of 1-128 ASCII letters, digits, hyphens or underscores is required.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await database.UnblockAsync(deviceId!, cancellationToken);
                return NoContent();
            }
            catch (Exception ex) when (IsStorageFailure(ex, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogError(ex, "Could not confirm unblocking device {DeviceId}.", deviceId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    "The device unblock could not be confirmed. Refresh the blocked-device list before retrying. If this continues, check the storage configuration.");
            }
        }

        private static bool IsStorageFailure(Exception exception, CancellationToken cancellationToken)
        {
            return exception is CosmosException or HttpRequestException or TimeoutException or AuthenticationFailedException or JsonException ||
                (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
        }
    }
}
