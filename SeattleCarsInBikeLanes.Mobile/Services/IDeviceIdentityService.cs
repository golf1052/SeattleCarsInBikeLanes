namespace SeattleCarsInBikeLanes.Mobile.Services;

public interface IDeviceIdentityService
{
    Task<string> GetDeviceIdAsync();
}
