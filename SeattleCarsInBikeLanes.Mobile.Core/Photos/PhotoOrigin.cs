using System.Text.Json.Serialization;

namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

/// <summary>
/// Where a photo the app knows about came from.
/// </summary>
public enum PhotoOrigin
{
    /// <summary>
    /// Taken with the app's camera and saved to the system photo library. The app created the
    /// asset, so it may edit it without the system asking the user for permission each time.
    /// </summary>
    [JsonStringEnumMemberName("captured")]
    Captured,

    /// <summary>
    /// Taken with the system camera and imported. The app does not own the asset.
    /// </summary>
    [JsonStringEnumMemberName("imported")]
    Imported,

    /// <summary>
    /// Taken with the app's camera and kept in the app's private persistent storage because the
    /// system photo library was unavailable.
    /// </summary>
    [JsonStringEnumMemberName("privateCaptured")]
    PrivateCaptured,

    /// <summary>
    /// Copied from the system picker into the app's private persistent storage.
    /// </summary>
    [JsonStringEnumMemberName("privateImported")]
    PrivateImported
}
