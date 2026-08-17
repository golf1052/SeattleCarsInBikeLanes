using CoreLocation;
using Foundation;
using Microsoft.Extensions.Logging;
using Photos;
using PhotosUI;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Services;
using UIKit;

namespace SeattleCarsInBikeLanes.Platforms.iOS;

/// <summary>
/// The Photos library, which is the app's only photo storage.
/// </summary>
public sealed class PhotoLibraryService : IPhotoLibraryService
{
    /// <summary>
    /// The album captured photos are filed into.
    /// </summary>
    /// <remarks>
    /// There is no API to ask for "assets this app created", so an album is how the app recognises
    /// its own photos later. It also means the user can find and manage them in Photos.
    /// </remarks>
    public const string AlbumTitle = "Cars in Bike Lanes";

    /// <summary>
    /// Identifies edits this app made, so they can be told apart from another app's adjustments.
    /// </summary>
    private const string AdjustmentFormatIdentifier = "com.golf1052.SeattleCarsInBikeLanes.uploadState";

    private const string AdjustmentFormatVersion = "1.0";

    /// <summary>
    /// How much of a photo to pull before giving up on finding its XMP.
    /// </summary>
    /// <remarks>
    /// XMP lives near the front of a JPEG. This is a backstop for a file whose structure we cannot
    /// make sense of, so a single odd photo cannot pull its whole multi megabyte self into memory.
    /// </remarks>
    private const int MaxUploadStateScanBytes = 1024 * 1024;

    private readonly ILogger<PhotoLibraryService> logger;

    public PhotoLibraryService(ILogger<PhotoLibraryService> logger)
    {
        this.logger = logger;
    }

    public bool SupportsWritingUploadState => true;

    public bool ConfirmsCapturedPhotoDeletion => true;

    public Task<PhotoLibraryAccess> RequestAccessAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<PhotoLibraryAccess> completion = new TaskCompletionSource<PhotoLibraryAccess>();

        PHPhotoLibrary.RequestAuthorization(PHAccessLevel.ReadWrite, status =>
        {
            completion.TrySetResult(status switch
            {
                PHAuthorizationStatus.Authorized => PhotoLibraryAccess.Granted,
                PHAuthorizationStatus.Limited => PhotoLibraryAccess.Limited,
                _ => PhotoLibraryAccess.Denied
            });
        });

        return completion.Task;
    }

    public Task<string?> SaveCapturedPhotoAsync(byte[] jpeg, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        TaskCompletionSource<string?> completion = new TaskCompletionSource<string?>();

        Task.Run(async () =>
        {
            try
            {
                PHAssetCollection? album = await GetOrCreateAlbumAsync();
                string? placeholderIdentifier = null;

                PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(() =>
                {
                    PHAssetCreationRequest creationRequest = PHAssetCreationRequest.CreationRequestForAsset();
                    creationRequest.AddResource(PHAssetResourceType.Photo, NSData.FromArray(jpeg), null);

                    PHObjectPlaceholder? placeholder = creationRequest.PlaceholderForCreatedAsset;
                    placeholderIdentifier = placeholder?.LocalIdentifier;

                    if (album is not null && placeholder is not null)
                    {
                        PHAssetCollectionChangeRequest? albumRequest =
                            PHAssetCollectionChangeRequest.ChangeRequest(album);
                        albumRequest?.AddAssets(new PHObject[] { placeholder });
                    }
                }, (success, error) =>
                {
                    if (!success)
                    {
                        logger.LogError("Failed to save a captured photo. {Error}", error?.LocalizedDescription);
                        completion.TrySetResult(null);
                        return;
                    }

                    completion.TrySetResult(placeholderIdentifier);
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to save a captured photo.");
                completion.TrySetResult(null);
            }
        }, cancellationToken);

        return completion.Task;
    }

    public async Task<IReadOnlyList<PhotoAsset>> GetCapturedPhotosAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        PHAssetCollection? album = await GetOrCreateAlbumAsync();
        if (album is null)
        {
            return Array.Empty<PhotoAsset>();
        }

        PHFetchOptions options = new PHFetchOptions()
        {
            SortDescriptors = new[] { new NSSortDescriptor("creationDate", ascending: false) },
            Predicate = NSPredicate.FromFormat("mediaType == %d", NSNumber.FromInt32((int)PHAssetMediaType.Image))
        };

        if (limit > 0)
        {
            options.FetchLimit = (nuint)limit;
        }

        PHFetchResult result = PHAsset.FetchAssets(album, options);
        return ToPhotoAssets(result);
    }

    public Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PhotoAsset>>(Array.Empty<PhotoAsset>());
        }

        PHFetchResult result = PHAsset.FetchAssetsUsingLocalIdentifiers(ids.ToArray(), null);
        IReadOnlyList<PhotoAsset> assets = ToPhotoAssets(result);

        // Fetching by identifier does not preserve the order that was asked for, and silently drops
        // anything the user has since deleted.
        Dictionary<string, PhotoAsset> byId = assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        List<PhotoAsset> ordered = new List<PhotoAsset>(assets.Count);
        foreach (string id in ids)
        {
            if (byId.TryGetValue(id, out PhotoAsset? asset))
            {
                ordered.Add(asset);
            }
        }

        return Task.FromResult<IReadOnlyList<PhotoAsset>>(ordered);
    }

    public Task<byte[]?> GetThumbnailAsync(string id, int pixelSize, CancellationToken cancellationToken = default)
    {
        PHAsset? asset = FindAsset(id);
        if (asset is null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        TaskCompletionSource<byte[]?> completion = new TaskCompletionSource<byte[]?>();

        PHImageRequestOptions options = new PHImageRequestOptions()
        {
            NetworkAccessAllowed = true,
            DeliveryMode = PHImageRequestOptionsDeliveryMode.Opportunistic,
            ResizeMode = PHImageRequestOptionsResizeMode.Fast
        };

        PHImageManager.DefaultManager.RequestImageForAsset(asset,
            new CoreGraphics.CGSize(pixelSize, pixelSize),
            PHImageContentMode.AspectFill,
            options,
            (image, info) =>
            {
                // Opportunistic delivery calls back more than once, first with a low quality
                // placeholder. Only the last call is worth keeping, and TrySetResult ignores the
                // rest.
                bool isDegraded = info?[PHImageKeys.ResultIsDegraded] is NSNumber degraded && degraded.BoolValue;
                if (isDegraded)
                {
                    return;
                }

                using NSData? jpeg = image?.AsJPEG(0.8f);
                completion.TrySetResult(jpeg?.ToArray());
            });

        return completion.Task;
    }

    public Task<byte[]?> GetPhotoDataAsync(string id, CancellationToken cancellationToken = default)
    {
        PHAsset? asset = FindAsset(id);
        if (asset is null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        TaskCompletionSource<byte[]?> completion = new TaskCompletionSource<byte[]?>();

        PHImageRequestOptions options = new PHImageRequestOptions()
        {
            NetworkAccessAllowed = true,
            DeliveryMode = PHImageRequestOptionsDeliveryMode.HighQualityFormat,
            Version = PHImageRequestOptionsVersion.Current
        };

        PHImageManager.DefaultManager.RequestImageDataAndOrientation(asset, options, (data, _, _, _) =>
        {
            completion.TrySetResult(data?.ToArray());
        });

        return completion.Task;
    }

    /// <summary>
    /// Streams the front of the photo and stops as soon as the answer is known.
    /// </summary>
    /// <remarks>
    /// The photo roll asks this for every visible photo. Pulling whole multi megabyte originals for
    /// a single boolean would make scrolling unusable, so this cancels the transfer the moment the
    /// scanner has seen enough.
    /// </remarks>
    public Task<XmpUploadState> ReadUploadStateAsync(string id, CancellationToken cancellationToken = default)
    {
        PHAsset? asset = FindAsset(id);
        if (asset is null)
        {
            return Task.FromResult(XmpUploadState.NotUploaded);
        }

        PHAssetResource? resource = PHAssetResource.GetAssetResources(asset)
            .FirstOrDefault(r => r.ResourceType == PHAssetResourceType.Photo)
            ?? PHAssetResource.GetAssetResources(asset).FirstOrDefault();

        if (resource is null)
        {
            return Task.FromResult(XmpUploadState.NotUploaded);
        }

        TaskCompletionSource<XmpUploadState> completion = new TaskCompletionSource<XmpUploadState>();
        MemoryStream buffer = new MemoryStream();
        object bufferLock = new object();

        // The data handler can run before RequestData has returned, so the request id may not exist
        // yet at the moment we decide to stop. cancelPending records that a cancel is owed so it can
        // be issued once the id is known. Everything here is touched from both PhotoKit's queue and
        // this one, so it is all read and written under bufferLock.
        int requestId = 0;
        bool finished = false;
        bool cancelPending = false;

        PHAssetResourceRequestOptions options = new PHAssetResourceRequestOptions()
        {
            NetworkAccessAllowed = true
        };

        void Finish(XmpUploadState state)
        {
            int idToCancel = 0;
            lock (bufferLock)
            {
                if (finished)
                {
                    return;
                }

                finished = true;

                if (requestId != 0)
                {
                    idToCancel = requestId;
                }
                else
                {
                    cancelPending = true;
                }
            }

            if (idToCancel != 0)
            {
                PHAssetResourceManager.DefaultManager.CancelDataRequest(idToCancel);
            }

            completion.TrySetResult(state);
        }

        int startedRequestId = PHAssetResourceManager.DefaultManager.RequestData(resource, options, data =>
        {
            XmpUploadState? resolved = null;

            lock (bufferLock)
            {
                if (finished)
                {
                    return;
                }

                byte[] chunk = data.ToArray();
                buffer.Write(chunk, 0, chunk.Length);
                buffer.Position = 0;

                JpegScanOutcome outcome = JpegSegmentScanner.TryFindXmpPacket(buffer, out byte[]? packet);
                buffer.Position = buffer.Length;

                if (outcome == JpegScanOutcome.Found)
                {
                    resolved = CarsInBikeLanesXmp.Read(CarsInBikeLanesXmp.TryParse(packet!));
                }
                else if (outcome != JpegScanOutcome.Incomplete || buffer.Length >= MaxUploadStateScanBytes)
                {
                    resolved = XmpUploadState.NotUploaded;
                }
            }

            if (resolved.HasValue)
            {
                Finish(resolved.Value);
            }
        }, error =>
        {
            if (error is not null)
            {
                logger.LogDebug("Reading the upload state for {Id} ended with {Error}.",
                    id, error.LocalizedDescription);
            }

            // Reaching the end without finding a packet means the photo was never stamped.
            Finish(XmpUploadState.NotUploaded);
        });

        bool cancelNow;
        lock (bufferLock)
        {
            requestId = startedRequestId;
            cancelNow = cancelPending;
        }

        if (cancelNow)
        {
            // The scan finished from the very first chunk, which is the common case, so the rest of
            // the transfer is only now cancellable.
            PHAssetResourceManager.DefaultManager.CancelDataRequest(startedRequestId);
        }

        return completion.Task;
    }

    public async Task<bool> WriteUploadStateAsync(string id,
        XmpUploadState state,
        CancellationToken cancellationToken = default)
    {
        PHAsset? asset = FindAsset(id);
        if (asset is null)
        {
            return false;
        }

        PHContentEditingInput? input = await RequestContentEditingInputAsync(asset);
        if (input?.FullSizeImageUrl is null)
        {
            logger.LogWarning("Could not open {Id} for editing, so its upload state was not written.", id);
            return false;
        }

        try
        {
            byte[] original = File.ReadAllBytes(input.FullSizeImageUrl.Path!);
            byte[] updated = JpegXmpEditor.SetUploadState(original, state);

            PHContentEditingOutput output = new PHContentEditingOutput(input)
            {
                AdjustmentData = new PHAdjustmentData(AdjustmentFormatIdentifier,
                    AdjustmentFormatVersion,
                    NSData.FromString(state.Uploaded ? "uploaded" : "notUploaded"))
            };

            File.WriteAllBytes(output.RenderedContentUrl.Path!, updated);

            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(() =>
            {
                PHAssetChangeRequest request = PHAssetChangeRequest.ChangeRequest(asset);
                request.ContentEditingOutput = output;
            }, (success, error) =>
            {
                if (!success)
                {
                    logger.LogWarning("Failed to write the upload state for {Id}. {Error}",
                        id, error?.LocalizedDescription);
                }

                completion.TrySetResult(success);
            });

            return await completion.Task;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write the upload state for {Id}.", id);
            return false;
        }
    }

    public Task<IReadOnlyList<string>> PickPhotosAsync(int limit, CancellationToken cancellationToken = default)
    {
        UIViewController? presenter = GetPresentingViewController();
        if (presenter is null)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        PHPickerConfiguration configuration = new PHPickerConfiguration(PHPhotoLibrary.SharedPhotoLibrary)
        {
            Filter = PHPickerFilter.ImagesFilter,
            SelectionLimit = limit
        };

        TaskCompletionSource<IReadOnlyList<string>> completion =
            new TaskCompletionSource<IReadOnlyList<string>>();

        PHPickerViewController picker = new PHPickerViewController(configuration)
        {
            Delegate = new PickerDelegate(completion)
        };

        presenter.PresentViewController(picker, animated: true, completionHandler: null);
        return completion.Task;
    }

    public Task ReleasePhotoAccessAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Deletes assets, letting PhotoKit put up its own confirmation.
    /// </summary>
    /// <remarks>
    /// iOS always asks the user before an app deletes a photo, even one the app created, so the app
    /// deliberately does not add a second confirmation of its own. Declining shows up here as a
    /// failed change request, which is why nothing is removed from the roll until this says so.
    /// </remarks>
    public Task<bool> DeletePhotosAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return Task.FromResult(true);
        }

        PHFetchResult assets = PHAsset.FetchAssetsUsingLocalIdentifiers(ids.ToArray(), null);
        PHAsset[] found = assets.OfType<PHAsset>().ToArray();
        if (found.Length == 0)
        {
            // Already gone, which is the state the caller was asking for.
            return Task.FromResult(true);
        }

        TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();

        PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
            () => PHAssetChangeRequest.DeleteAssets(found),
            (success, error) =>
            {
                if (!success)
                {
                    logger.LogInformation("Deleting {Count} photo(s) did not go through. {Error}",
                        ids.Count, error?.LocalizedDescription);
                }

                completion.TrySetResult(success);
            });

        return completion.Task;
    }

    private static IReadOnlyList<PhotoAsset> ToPhotoAssets(PHFetchResult result)
    {
        List<PhotoAsset> assets = new List<PhotoAsset>((int)result.Count);
        foreach (PHAsset asset in result.OfType<PHAsset>())
        {
            DateTimeOffset? createdAt = asset.CreationDate is null
                ? null
                : (DateTimeOffset)(DateTime)asset.CreationDate;

            GeoPosition? location = null;
            if (asset.Location is CLLocation clLocation)
            {
                location = new GeoPosition(clLocation.Coordinate.Latitude, clLocation.Coordinate.Longitude);
            }

            assets.Add(new PhotoAsset(asset.LocalIdentifier, createdAt, location));
        }

        return assets;
    }

    private static PHAsset? FindAsset(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return PHAsset.FetchAssetsUsingLocalIdentifiers(new[] { id }, null).OfType<PHAsset>().FirstOrDefault();
    }

    private static Task<PHContentEditingInput?> RequestContentEditingInputAsync(PHAsset asset)
    {
        TaskCompletionSource<PHContentEditingInput?> completion =
            new TaskCompletionSource<PHContentEditingInput?>();

        PHContentEditingInputRequestOptions options = new PHContentEditingInputRequestOptions()
        {
            NetworkAccessAllowed = true,

            // Returning true means an earlier edit of ours is replaced rather than stacked on, so
            // repeated writes do not accumulate adjustments.
            CanHandleAdjustmentData = _ => true
        };

        asset.RequestContentEditingInput(options, (input, _) => completion.TrySetResult(input));
        return completion.Task;
    }

    private async Task<PHAssetCollection?> GetOrCreateAlbumAsync()
    {
        PHAssetCollection? existing = FindAlbum();
        if (existing is not null)
        {
            return existing;
        }

        TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
        PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
            () => PHAssetCollectionChangeRequest.CreateAssetCollection(AlbumTitle),
            (success, error) =>
            {
                if (!success)
                {
                    logger.LogError("Failed to create the {Album} album. {Error}",
                        AlbumTitle, error?.LocalizedDescription);
                }

                completion.TrySetResult(success);
            });

        return await completion.Task ? FindAlbum() : null;
    }

    private static PHAssetCollection? FindAlbum()
    {
        PHFetchOptions options = new PHFetchOptions()
        {
            Predicate = NSPredicate.FromFormat("title = %@", new NSString(AlbumTitle))
        };

        return PHAssetCollection.FetchAssetCollections(PHAssetCollectionType.Album,
            PHAssetCollectionSubtype.AlbumRegular,
            options).OfType<PHAssetCollection>().FirstOrDefault();
    }

    private static UIViewController? GetPresentingViewController()
    {
        UIWindow? window = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);

        UIViewController? controller = window?.RootViewController;
        while (controller?.PresentedViewController is not null)
        {
            controller = controller.PresentedViewController;
        }

        return controller;
    }

    private sealed class PickerDelegate : PHPickerViewControllerDelegate
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> completion;

        public PickerDelegate(TaskCompletionSource<IReadOnlyList<string>> completion)
        {
            this.completion = completion;
        }

        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            picker.DismissViewController(animated: true, completionHandler: null);

            // A result without an identifier is a photo the picker will only hand over as raw data,
            // which the app cannot reference in place, so it is skipped.
            List<string> identifiers = results
                .Select(result => result.AssetIdentifier)
                .Where(identifier => !string.IsNullOrEmpty(identifier))
                .ToList()!;

            completion.TrySetResult(identifiers);
        }
    }
}
