using SeattleCarsInBikeLanes.Mobile.Core.Metadata;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Stands in on platforms where the photo library has not been implemented.
/// </summary>
/// <remarks>
/// The app is iOS first. This keeps the other target frameworks compiling and, more importantly,
/// makes an unimplemented platform fail visibly at the point of use instead of silently behaving as
/// though the user has no photos.
/// </remarks>
public sealed class UnsupportedPhotoLibraryService : IPhotoLibraryService
{
    public bool SupportsWritingUploadState => false;

    public bool ConfirmsCapturedPhotoDeletion => false;

    public Task<PhotoLibraryAccess> CheckAccessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PhotoLibraryAccess.Denied);

    public Task<PhotoLibraryAccess> RequestAccessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PhotoLibraryAccess.Denied);

    public Task<string?> SaveCapturedPhotoAsync(byte[] jpeg, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("Saving photos is only implemented on iOS.");

    public Task<IReadOnlyList<PhotoAsset>> GetCapturedPhotosAsync(int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PhotoAsset>>(Array.Empty<PhotoAsset>());

    public Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PhotoAsset>>(Array.Empty<PhotoAsset>());

    public Task<byte[]?> GetThumbnailAsync(string id, int pixelSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<byte[]?> GetPhotoDataAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<XmpUploadState> ReadUploadStateAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(XmpUploadState.NotUploaded);

    public Task<bool> WriteUploadStateAsync(string id,
        XmpUploadState state,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<string>> PickPhotosAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task ReleasePhotoAccessAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<bool> DeletePhotosAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
