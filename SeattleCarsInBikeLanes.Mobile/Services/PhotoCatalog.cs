using System.Collections.Concurrent;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// A photo as the app presents it: an asset plus where it came from and whether it has been sent.
/// </summary>
public sealed class ReportPhoto : IPhotoMoment
{
    public required string Id { get; init; }

    public required PhotoOrigin Origin { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public GeoPosition? Location { get; init; }

    public bool Submitted { get; init; }
}

public readonly record struct ForgetImportedPhotosResult(int Removed, int Retained);

/// <summary>
/// The photos the app knows about, and their submitted state.
/// </summary>
/// <remarks>
/// This is the one place that routes system-library assets, private files, and imported-photo state.
/// Everything above it works with one photo model.
/// </remarks>
public interface IPhotoCatalog
{
    Task<IReadOnlyList<ReportPhoto>> GetPhotosAsync(int capturedLimit = 100,
        CancellationToken cancellationToken = default);

    Task<ReportPhoto?> AddCapturedPhotoAsync(byte[] jpeg, CancellationToken cancellationToken = default);

    Task<byte[]?> GetThumbnailAsync(string id,
        int pixelSize,
        CancellationToken cancellationToken = default);

    Task<byte[]?> GetPhotoDataAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the picker and records whatever the user chose.
    /// </summary>
    /// <returns>The photos that were added.</returns>
    Task<IReadOnlyList<ReportPhoto>> ImportPhotosAsync(int limit, CancellationToken cancellationToken = default);

    Task<ForgetImportedPhotosResult> ForgetImportedPhotosAsync(
        IReadOnlySet<string> retainedIds,
        CancellationToken cancellationToken = default);

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
    private readonly IPrivatePhotoStore privatePhotos;
    private readonly IImportedPhotoStore importedPhotos;
    private readonly IImageResizer imageResizer;
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
        IPrivatePhotoStore privatePhotos,
        IImportedPhotoStore importedPhotos,
        IImageResizer imageResizer,
        ILogger<PhotoCatalog> logger)
    {
        this.photoLibrary = photoLibrary;
        this.privatePhotos = privatePhotos;
        this.importedPhotos = importedPhotos;
        this.imageResizer = imageResizer;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<ReportPhoto>> GetPhotosAsync(int capturedLimit = 100,
        CancellationToken cancellationToken = default)
    {
        PhotoLibraryAccess access = await CheckLibraryAccessAsync(cancellationToken);
        IReadOnlyList<PhotoAsset> captured = access == PhotoLibraryAccess.Granted
            ? await photoLibrary.GetCapturedPhotosAsync(capturedLimit, cancellationToken)
            : Array.Empty<PhotoAsset>();
        IReadOnlyList<ImportedPhoto> imported = access == PhotoLibraryAccess.Granted
            ? await importedPhotos.GetAllAsync()
            : Array.Empty<ImportedPhoto>();
        IReadOnlyList<PrivatePhotoAsset> privateAssets =
            await privatePhotos.GetPhotosAsync(cancellationToken);

        List<ReportPhoto> photos =
            new List<ReportPhoto>(captured.Count + imported.Count + privateAssets.Count);

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

        foreach (PrivatePhotoAsset asset in privateAssets)
        {
            photos.Add(new ReportPhoto()
            {
                Id = asset.Id,
                Origin = asset.Imported ? PhotoOrigin.PrivateImported : PhotoOrigin.PrivateCaptured,
                CreatedAt = asset.CreatedAt,
                Location = asset.Location,
                Submitted = await ReadUploadStateAsync(
                    asset.Id,
                    PhotoOrigin.PrivateCaptured,
                    cancellationToken)
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
        PhotoLibraryAccess access = await CheckLibraryAccessAsync(cancellationToken);
        if (access == PhotoLibraryAccess.Granted)
        {
            string? libraryId = await photoLibrary.SaveCapturedPhotoAsync(jpeg, cancellationToken);
            if (libraryId is not null)
            {
                uploadStateCache[libraryId] = false;

                IReadOnlyList<PhotoAsset> saved =
                    await photoLibrary.GetPhotosAsync(new[] { libraryId }, cancellationToken);
                PhotoAsset? asset = saved.FirstOrDefault();

                return new ReportPhoto()
                {
                    Id = libraryId,
                    Origin = PhotoOrigin.Captured,
                    CreatedAt = asset?.CreatedAt ?? DateTimeOffset.Now,
                    Location = asset?.Location,
                    Submitted = false
                };
            }

            logger.LogWarning("The system photo library did not save a capture; using private storage.");
        }

        PrivatePhotoAsset? privateAsset =
            await privatePhotos.SaveAsync(jpeg, imported: false, cancellationToken);
        if (privateAsset is null)
        {
            return null;
        }

        uploadStateCache[privateAsset.Id] = false;

        return new ReportPhoto()
        {
            Id = privateAsset.Id,
            Origin = PhotoOrigin.PrivateCaptured,
            CreatedAt = privateAsset.CreatedAt ?? DateTimeOffset.Now,
            Location = privateAsset.Location,
            Submitted = false
        };
    }

    public async Task<byte[]?> GetThumbnailAsync(string id,
        int pixelSize,
        CancellationToken cancellationToken = default)
    {
        if (!IsPrivateId(id))
        {
            return await photoLibrary.GetThumbnailAsync(id, pixelSize, cancellationToken);
        }

        byte[]? jpeg = await privatePhotos.GetDataAsync(id, cancellationToken);
        return jpeg is null
            ? null
            : await imageResizer.ResizeAsync(jpeg, pixelSize, cancellationToken);
    }

    public Task<byte[]?> GetPhotoDataAsync(string id, CancellationToken cancellationToken = default) =>
        IsPrivateId(id)
            ? privatePhotos.GetDataAsync(id, cancellationToken)
            : photoLibrary.GetPhotoDataAsync(id, cancellationToken);

    public async Task<IReadOnlyList<ReportPhoto>> ImportPhotosAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PickedPhoto> picked = await photoLibrary.PickPhotosAsync(limit, cancellationToken);
        if (picked.Count == 0)
        {
            return Array.Empty<ReportPhoto>();
        }

        List<string> ids = picked
            .Select(photo => photo.LibraryId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        if (ids.Count > 0)
        {
            await importedPhotos.AddAsync(ids);
        }

        IReadOnlyList<PhotoAsset> assets = ids.Count > 0
            ? await photoLibrary.GetPhotosAsync(ids, cancellationToken)
            : Array.Empty<PhotoAsset>();
        List<ReportPhoto> imported = new List<ReportPhoto>(picked.Count);
        Dictionary<string, PhotoAsset> assetsById = assets
            .GroupBy(asset => asset.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (PickedPhoto selected in picked)
        {
            if (!string.IsNullOrWhiteSpace(selected.LibraryId) &&
                assetsById.TryGetValue(selected.LibraryId, out PhotoAsset? asset))
            {
                imported.Add(new ReportPhoto()
                {
                    Id = asset.Id,
                    Origin = PhotoOrigin.Imported,
                    CreatedAt = asset.CreatedAt,
                    Location = asset.Location,
                    Submitted = await ReadUploadStateAsync(
                        asset.Id,
                        PhotoOrigin.Imported,
                        cancellationToken)
                });
                continue;
            }

            if (selected.Jpeg is null)
            {
                continue;
            }

            PrivatePhotoAsset privateAsset =
                await privatePhotos.SaveAsync(selected.Jpeg, imported: true, cancellationToken);
            imported.Add(new ReportPhoto()
            {
                Id = privateAsset.Id,
                Origin = PhotoOrigin.PrivateImported,
                CreatedAt = privateAsset.CreatedAt,
                Location = privateAsset.Location,
                Submitted = (await privatePhotos.ReadUploadStateAsync(
                    privateAsset.Id,
                    cancellationToken)).Uploaded
            });
        }

        return imported;
    }

    public async Task<ForgetImportedPhotosResult> ForgetImportedPhotosAsync(
        IReadOnlySet<string> retainedIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedIds);

        IReadOnlyList<ImportedPhoto> systemImported = await importedPhotos.GetAllAsync();
        IReadOnlyList<PrivatePhotoAsset> privateImported =
            (await privatePhotos.GetPhotosAsync(cancellationToken))
            .Where(photo => photo.Imported)
            .ToList();

        List<ReportPhoto> all = systemImported
            .Select(photo => new ReportPhoto()
            {
                Id = photo.LocalIdentifier,
                Origin = PhotoOrigin.Imported
            })
            .Concat(privateImported.Select(photo => new ReportPhoto()
            {
                Id = photo.Id,
                Origin = PhotoOrigin.PrivateImported
            }))
            .ToList();

        List<ReportPhoto> removable = all
            .Where(photo => !retainedIds.Contains(photo.Id))
            .ToList();
        IReadOnlyList<ReportPhoto> removed = await DeleteAsync(removable, cancellationToken);
        return new ForgetImportedPhotosResult(removed.Count, all.Count - removed.Count);
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

        foreach (ReportPhoto privatePhoto in photos.Where(photo =>
                     photo.Origin is PhotoOrigin.PrivateCaptured or PhotoOrigin.PrivateImported))
        {
            bool written = await privatePhotos.WriteUploadStateAsync(
                privatePhoto.Id,
                state,
                cancellationToken);
            uploadStateCache[privatePhoto.Id] = true;
            if (!written)
            {
                logger.LogWarning("Could not stamp private photo {Id} as submitted.", privatePhoto.Id);
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

        List<string> ids = photos
            .Where(photo => photo.Origin is PhotoOrigin.Captured or PhotoOrigin.Imported)
            .Select(photo => photo.Id)
            .ToList();
        await importedPhotos.RemoveAsync(ids);
        await photoLibrary.ReleasePhotoAccessAsync(
            photos.Where(photo => photo.Origin == PhotoOrigin.Imported)
                .Select(photo => photo.Id)
                .ToList(),
            cancellationToken);

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
        List<ReportPhoto> privateStored = photos.Where(photo =>
            photo.Origin is PhotoOrigin.PrivateCaptured or PhotoOrigin.PrivateImported).ToList();

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

        foreach (ReportPhoto photo in privateStored)
        {
            if (await privatePhotos.DeleteAsync(photo.Id, cancellationToken))
            {
                removed.Add(photo);
            }
        }

        // A captured photo can also carry an imported record, from the fallback that runs when its
        // own metadata could not be stamped, so every id is offered to the store.
        await ForgetAsync(removed, cancellationToken);

        return removed;
    }

    private async Task<bool> ReadCapturedUploadStateAsync(string id, CancellationToken cancellationToken) =>
        await ReadUploadStateAsync(id, PhotoOrigin.Captured, cancellationToken);

    private async Task<bool> ReadUploadStateAsync(string id,
        PhotoOrigin origin,
        CancellationToken cancellationToken)
    {
        if (uploadStateCache.TryGetValue(id, out bool cached))
        {
            return cached;
        }

        XmpUploadState state = origin is PhotoOrigin.PrivateCaptured or PhotoOrigin.PrivateImported ||
            IsPrivateId(id)
            ? await privatePhotos.ReadUploadStateAsync(id, cancellationToken)
            : await photoLibrary.ReadUploadStateAsync(id, cancellationToken);
        uploadStateCache[id] = state.Uploaded;
        return state.Uploaded;
    }

    private static bool IsPrivateId(string id) =>
        id.StartsWith("private:", StringComparison.Ordinal);

    private async Task<PhotoLibraryAccess> CheckLibraryAccessAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await photoLibrary.CheckAccessAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check photo-library access; using private storage.");
            return PhotoLibraryAccess.Denied;
        }
    }
}
