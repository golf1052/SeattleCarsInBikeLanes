namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// The identifier the site uses to tell devices apart.
/// </summary>
/// <remarks>
/// This exists so abuse can be dealt with by blocking a device rather than by taking uploads away
/// from everyone. It is not an account, it identifies nobody, and it is only ever sent to this
/// app's own backend.
/// </remarks>
/// <inheritdoc />
public sealed class DeviceIdentityService : IDeviceIdentityService
{
    private const string StorageKey = "cbl.device-id";

    private readonly ILogger<DeviceIdentityService> logger;
    private readonly SemaphoreSlim mutex = new SemaphoreSlim(1, 1);
    private string? cached;

    public DeviceIdentityService(ILogger<DeviceIdentityService> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Returns the device's identifier, generating and storing one on first use.
    /// </summary>
    /// <remarks>
    /// The identifier is a random GUID kept in the keychain rather than the vendor identifier iOS
    /// offers. The vendor identifier resets once the user removes every app from the same vendor,
    /// which makes it useless for the one thing this is for. The keychain value survives a
    /// reinstall.
    /// </remarks>
    public async Task<string> GetDeviceIdAsync()
    {
        if (cached is not null)
        {
            return cached;
        }

        await mutex.WaitAsync();
        try
        {
            if (cached is not null)
            {
                return cached;
            }

            try
            {
                string? stored = await SecureStorage.Default.GetAsync(StorageKey);
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    cached = stored;
                    return cached;
                }

                string generated = Guid.NewGuid().ToString();
                await SecureStorage.Default.SetAsync(StorageKey, generated);
                cached = generated;
                return cached;
            }
            catch (Exception ex)
            {
                // A device with no usable keychain still deserves a working app, so fall back to
                // an identifier that at least lasts for this install.
                logger.LogWarning(ex, "Could not use secure storage for the device id.");
                cached = FallbackDeviceId();
                return cached;
            }
        }
        finally
        {
            mutex.Release();
        }
    }

    private static string FallbackDeviceId()
    {
        string? existing = Preferences.Default.Get<string?>(StorageKey, null);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        string generated = Guid.NewGuid().ToString();
        Preferences.Default.Set(StorageKey, generated);
        return generated;
    }
}
