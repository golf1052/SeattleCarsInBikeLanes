namespace SeattleCarsInBikeLanes.Models
{
    public sealed class BlockDeviceRequest
    {
        public string? DeviceId { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class UnblockDeviceRequest
    {
        public string? DeviceId { get; set; }
    }

    public sealed record AdminBlockedDeviceResponse(string DeviceId, string Reason);
}
