using System.Collections.Concurrent;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// A photo as the app presents it: an asset plus where it came from and whether it has been sent.
/// </summary>
public sealed class ReportPhoto
{
    public required string Id { get; init; }

    public required PhotoOrigin Origin { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public GeoPosition? Location { get; init; }

    public bool Submitted { get; init; }
}

/// <summary>
/// The photos the app knows about, and their submitted state.
/// </summary>
/// <remarks>
/// This is the one place that knows the submitted flag is stored in two different ways. Everything
/// above it just asks whether a photo has been submitted.
/// </remarks>
public interface IPhotoCatalog
{
    Task<IReadOnlyList<ReportPhoto>> GetPhotosAsync(int capturedLimit = 100,
        CancellationToken cancellationToken = default);

    Task<ReportPhoto?> AddCapturedPhotoAsync(byte[] jpeg, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the picker and records whatever the user chose.
    /// </summary>
    /// <returns>The photos that were added.</returns>
    Task<IReadOnlyList<ReportPhoto>> ImportPhotosAsync(int limit, CancellationToken cancellationToken = default);

    Task MarkSubmittedAsync(IReadOnlyList<ReportPhoto> photos,
        string? submissionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes photos from the app's view of the world.
    /// </summary>
    /// <remarks>
    /// Imported photos are only forgotten, never deleted: the app does not own them and the user
    /// did not ask for them to be erased from their library.
    /// </remarks>
    Task ForgetAsync(IReadOnlyList<ReportPhoto> photos, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets rid of photos for good: captured ones are deleted, imported ones are forgotten.
    /// </summary>
    /// <remarks>
    /// The app created the captured photos, so deleting them is what the user means. It did not
    /// create the imported ones, which live wherever the user already keeps them, so those are only
    /// dropped from the app's list.
    /// </remarks>
    /// <returns>The photos that are no longer in the roll, which is empty if the user declined.</returns>
    Task<IReadOnlyList<ReportPhoto>> DeleteAsync(IReadOnlyList<ReportPhoto> photos,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class PhotoCatalog : IPhotoCatalog
{
    private readonly IPhotoLibraryService photoLibrary;
    private readonly IImportedPhotoStore importedPhotos;
    private readonly ILogger<PhotoCatalog> logger;

    /// <summary>
    /// Upload state keyed by asset id, so scrolling the roll does not re-read the same photos.
    /// </summary>
    /// <remarks>
    /// Concurrent because reports are now sent from a background queue, so the flag is written from
    /// a worker thread while the roll is being read on the UI thread.
    /// </remarks>
    private readonly ConcurrentDictionary<string, bool> uploadStateCache =
        new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

    public PhotoCatalog(IPhotoLibraryService photoLibrary,
        IImportedPhotoStore importedPhotos,
        ILogger<PhotoCatalog> logger)
    {
        this.photoLibrary = photoLibrary;
        this.importedPhotos = importedPhotos;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<ReportPhoto>> GetPhotosAsync(int capturedLimit = 100,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PhotoAsset> captured = await photoLibrary.GetCapturedPhotosAsync(capturedLimit, cancellationToken);
        IReadOnlyList<ImportedPhoto> imported = await importedPhotos.GetAllAsync();

        List<ReportPhoto> photos = new List<ReportPhoto>(captured.Count + imported.Count);

        foreach (PhotoAsset asset in captured)
        {
            photos.Add(new ReportPhoto()
            {
                Id = asset.Id,
                Origin = PhotoOrigin.Captured,
                CreatedAt = asset.CreatedAt,
                Location = asset.Location,
                Submitted = await ReadCapturedUploadStateAsync(asset.Id, cancellationToken)
            });
        }

        if (imported.Count > 0)
        {
            IReadOnlyList<PhotoAsset> importedAssets = await photoLibrary.GetPhotosAsync(
                imported.Select(photo => photo.LocalIdentifier).ToList(),
                cancellationToken);

            Dictionary<string, ImportedPhoto> state =
                imported.ToDictionary(photo => photo.LocalIdentifier, StringComparer.Ordinal);

            // Assets missing from the fetch were deleted from the library, so stop tracking them.
            HashSet<string> stillPresent = importedAssets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
            List<string> vanished = imported
                .Where(photo => !stillPresent.Contains(photo.LocalIdentifier))
                .Select(photo => photo.LocalIdentifier)
                .ToList();

            if (vanished.Count > 0)
            {
                await importedPhotos.RemoveAsync(vanished);
            }

            foreach (PhotoAsset asset in importedAssets)
            {
                bool submitted = state.TryGetValue(asset.Id, out ImportedPhoto? record) && record.Submitted;

                // A photo the app captured, exported, and had re-imported still carries its own
                // flag, and the photo is the more trustworthy of the two.
                if (!submitted)
                {
                    submitted = await ReadCapturedUploadStateAsync(asset.Id, cancellationToken);
                }

                photos.Add(new ReportPhoto()
                {
                    Id = asset.Id,
                    Origin = PhotoOrigin.Imported,
                    CreatedAt = asset.CreatedAt,
                    Location = asset.Location,
                    Submitted = submitted
                });
            }
        }

        return photos
            .GroupBy(photo => photo.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(photo => photo.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public async Task<ReportPhoto?> AddCapturedPhotoAsync(byte[] jpeg, CancellationToken cancellationToken = default)
    {
        string? id = await photoLibrary.SaveCapturedPhotoAsync(jpeg, cancellationToken);
        if (id is null)
        {
            return null;
        }

        uploadStateCache[id] = false;

        IReadOnlyList<PhotoAsset> saved = await photoLibrary.GetPhotosAsync(new[] { id }, cancellationToken);
        PhotoAsset? asset = saved.FirstOrDefault();

        return new ReportPhoto()
        {
            Id = id,
            Origin = PhotoOrigin.Captured,
            CreatedAt = asset?.CreatedAt ?? DateTimeOffset.Now,
            Location = asset?.Location,
            Submitted = false
        };
    }

    public async Task<IReadOnlyList<ReportPhoto>> ImportPhotosAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> ids = await photoLibrary.PickPhotosAsync(limit, cancellationToken);
        if (ids.Count == 0)
        {
            return Array.Empty<ReportPhoto>();
        }

        await importedPhotos.AddAsync(ids);

        IReadOnlyList<PhotoAsset> assets = await photoLibrary.GetPhotosAsync(ids, cancellationToken);
        List<ReportPhoto> imported = new List<ReportPhoto>(assets.Count);

        foreach (PhotoAsset asset in assets)
        {
            imported.Add(new ReportPhoto()
            {
                Id = asset.Id,
                Origin = PhotoOrigin.Imported,
                CreatedAt = asset.CreatedAt,
                Location = asset.Location,
                Submitted = await ReadCapturedUploadStateAsync(asset.Id, cancellationToken)
            });
        }

        return imported;
    }

    public async Task MarkSubmittedAsync(IReadOnlyList<ReportPhoto> photos,
        string? submissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);

        XmpUploadState state = new XmpUploadState(true, DateTimeOffset.UtcNow, submissionId);
        List<string> fallbackToIndex = new List<string>();

        foreach (ReportPhoto photo in photos.Where(photo => photo.Origin == PhotoOrigin.Captured))
        {
            bool written = await photoLibrary.WriteUploadStateAsync(photo.Id, state, cancellationToken);
            uploadStateCache[photo.Id] = true;

            if (!written)
            {
                // The report did upload, so losing the flag would invite a duplicate submission.
                // Recording it alongside the imported photos is worse than the photo carrying it,
                // but much better than forgetting.
                logger.LogWarning("Could not stamp {Id} as submitted, falling back to the local index.", photo.Id);
                fallbackToIndex.Add(photo.Id);
            }
        }

        List<string> importedIds = photos
            .Where(photo => photo.Origin == PhotoOrigin.Imported)
            .Select(photo => photo.Id)
            .Concat(fallbackToIndex)
            .ToList();

        if (importedIds.Count > 0)
        {
            await importedPhotos.AddAsync(importedIds);
            await importedPhotos.MarkSubmittedAsync(importedIds, submissionId);

            foreach (string id in importedIds)
            {
                uploadStateCache[id] = true;
            }
        }
    }

    public async Task ForgetAsync(IReadOnlyList<ReportPhoto> photos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);

        List<string> ids = photos.Select(photo => photo.Id).ToList();
        await importedPhotos.RemoveAsync(ids);

        foreach (string id in ids)
        {
            uploadStateCache.TryRemove(id, out _);
        }
    }

    public async Task<IReadOnlyList<ReportPhoto>> DeleteAsync(IReadOnlyList<ReportPhoto> photos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);

        if (photos.Count == 0)
        {
            return Array.Empty<ReportPhoto>();
        }

        List<ReportPhoto> captured = photos.Where(photo => photo.Origin == PhotoOrigin.Captured).ToList();
        List<ReportPhoto> imported = photos.Where(photo => photo.Origin == PhotoOrigin.Imported).ToList();

        List<ReportPhoto> removed = new List<ReportPhoto>(photos.Count);

        if (captured.Count > 0)
        {
            // The user is asked by the platform, and saying no has to leave the roll untouched.
            bool deleted = await photoLibrary.DeletePhotosAsync(
                captured.Select(photo => photo.Id).ToList(),
                cancellationToken);

            if (!deleted)
            {
                logger.LogInformation("Deleting {Count} captured photo(s) was declined or failed.", captured.Count);
                return Array.Empty<ReportPhoto>();
            }

            removed.AddRange(captured);
        }

        if (imported.Count > 0)
        {
            removed.AddRange(imported);
        }

        // A captured photo can also carry an imported record, from the fallback that runs when its
        // own metadata could not be stamped, so every id is offered to the store.
        await ForgetAsync(removed, cancellationToken);

        return removed;
    }

    private async Task<bool> ReadCapturedUploadStateAsync(string id, CancellationToken cancellationToken)
    {
        if (uploadStateCache.TryGetValue(id, out bool cached))
        {
            return cached;
        }

        XmpUploadState state = await photoLibrary.ReadUploadStateAsync(id, cancellationToken);
        uploadStateCache[id] = state.Uploaded;
        return state.Uploaded;
    }
}
