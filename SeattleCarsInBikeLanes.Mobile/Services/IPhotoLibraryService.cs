using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Where a photo the app knows about came from.
/// </summary>
public enum PhotoOrigin
{
    /// <summary>
    /// Taken with the app's camera. The app created the asset, so it may edit it without the
    /// system asking the user for permission each time.
    /// </summary>
    Captured,

    /// <summary>
    /// Taken with the system camera and imported. The app does not own the asset.
    /// </summary>
    Imported
}

/// <summary>
/// A photo in the device's photo library.
/// </summary>
/// <param name="Id">The Photos framework local identifier.</param>
public sealed record PhotoAsset(string Id, DateTimeOffset? CreatedAt, GeoPosition? Location);

/// <summary>
/// The level of access the user has granted to their photo library.
/// </summary>
public enum PhotoLibraryAccess
{
    NotDetermined,

    Granted,

    /// <summary>
    /// The user picked specific photos to share. The app's own album is invisible under this mode,
    /// so the photo roll cannot work and the user has to be told why.
    /// </summary>
    Limited,

    Denied
}

/// <summary>
/// The device's photo library, which is where this app keeps everything.
/// </summary>
/// <remarks>
/// The app deliberately has no photo storage of its own. Captured photos are written into a
/// dedicated album so they are visible and manageable in Photos, and imported photos are referenced
/// where they already live rather than copied.
/// </remarks>
public interface IPhotoLibraryService
{
    /// <summary>
    /// Whether the platform can persist the upload flag into the photo itself.
    /// </summary>
    bool SupportsWritingUploadState { get; }

    /// <summary>
    /// Whether deleting captured photos presents a platform-owned confirmation.
    /// </summary>
    bool ConfirmsCapturedPhotoDeletion { get; }

    Task<PhotoLibraryAccess> CheckAccessAsync(CancellationToken cancellationToken = default);

    Task<PhotoLibraryAccess> RequestAccessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a photo taken in the app and adds it to the app's album.
    /// </summary>
    /// <returns>The new asset's identifier, or null if it could not be saved.</returns>
    Task<string?> SaveCapturedPhotoAsync(byte[] jpeg, CancellationToken cancellationToken = default);

    /// <summary>
    /// The photos in the app's album, newest first.
    /// </summary>
    Task<IReadOnlyList<PhotoAsset>> GetCapturedPhotosAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up specific assets, skipping any that no longer exist.
    /// </summary>
    Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);

    Task<byte[]?> GetThumbnailAsync(string id, int pixelSize, CancellationToken cancellationToken = default);

    Task<byte[]?> GetPhotoDataAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the app's upload flag out of the photo, reading as little of it as possible.
    /// </summary>
    Task<Core.Metadata.XmpUploadState> ReadUploadStateAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the app's upload flag into the photo.
    /// </summary>
    /// <returns>False when the photo could not be updated, which the caller has to cope with.</returns>
    Task<bool> WriteUploadStateAsync(string id,
        Core.Metadata.XmpUploadState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the system photo picker.
    /// </summary>
    /// <returns>The identifiers of the chosen assets.</returns>
    Task<IReadOnlyList<string>> PickPhotosAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases persistent access to imported photos the app no longer tracks.
    /// </summary>
    Task ReleasePhotoAccessAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes assets from the photo library.
    /// </summary>
    /// <remarks>
    /// The platform is expected to confirm with the user, since this destroys their photos and the
    /// app is not the only thing that can see them.
    /// </remarks>
    /// <returns>False when the user declined or the deletion failed.</returns>
    Task<bool> DeletePhotosAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
}
