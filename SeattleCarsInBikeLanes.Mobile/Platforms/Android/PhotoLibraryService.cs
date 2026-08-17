using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Android.Content;
using Android.Graphics;
using Android.Provider;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Services;
using AndroidSize = global::Android.Util.Size;
using AndroidUri = Android.Net.Uri;
using CancellationSignal = global::Android.OS.CancellationSignal;
using OperationCanceledException = System.OperationCanceledException;
using ParcelFileDescriptor = global::Android.OS.ParcelFileDescriptor;

namespace SeattleCarsInBikeLanes.Platforms.Android;

/// <summary>
/// Stores captured photos in Android's shared MediaStore and retains references to imported photos.
/// </summary>
public sealed class PhotoLibraryService : IPhotoLibraryService
{
    public const string AlbumTitle = "Cars in Bike Lanes";

    private const string FileNamePrefix = "CarsInBikeLanes-";
    private const string MediaDocumentsAuthority = "com.android.providers.media.documents";
    private const int ThumbnailQuality = 85;
    private static readonly AndroidUri CapturedPhotoCollection =
        MediaStore.Images.Media.ExternalContentUri!;
    private static readonly string RelativePath =
        global::Android.OS.Environment.DirectoryPictures + $"/{AlbumTitle}/";

    private readonly Context context;
    private readonly ContentResolver resolver;
    private readonly ILogger<PhotoLibraryService> logger;

    public PhotoLibraryService(ILogger<PhotoLibraryService> logger)
    {
        this.logger = logger;
        context = global::Android.App.Application.Context;
        resolver = context.ContentResolver
            ?? throw new InvalidOperationException("Android did not provide a content resolver.");
    }

    public bool SupportsWritingUploadState => true;

    public bool ConfirmsCapturedPhotoDeletion => false;

    public Task<PhotoLibraryAccess> CheckAccessAsync(CancellationToken cancellationToken = default) =>
        RequestAccessAsync(cancellationToken);

    public Task<PhotoLibraryAccess> RequestAccessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Scoped storage always lets an app manage media it created. Imported media is granted
        // individually by ACTION_OPEN_DOCUMENT, so no broad library permission is needed.
        return Task.FromResult(PhotoLibraryAccess.Granted);
    }

    public async Task<string?> SaveCapturedPhotoAsync(byte[] jpeg,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        cancellationToken.ThrowIfCancellationRequested();

        ContentValues values = new ContentValues();
        values.Put(ImageColumns.DisplayName,
            $"{FileNamePrefix}{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.jpg");
        values.Put(ImageColumns.MimeType, "image/jpeg");
        values.Put(ImageColumns.RelativePath, RelativePath);
        values.Put(ImageColumns.DateTaken, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        values.Put(ImageColumns.IsPending, 1);

        AndroidUri? item = null;
        try
        {
            item = resolver.Insert(CapturedPhotoCollection, values);
            if (item is null)
            {
                logger.LogError("MediaStore did not create a row for a captured photo.");
                return null;
            }

            await using (Stream? output = resolver.OpenOutputStream(item, "w"))
            {
                if (output is null)
                {
                    throw new IOException("MediaStore did not open the captured photo for writing.");
                }

                await output.WriteAsync(jpeg, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            ContentValues ready = new ContentValues();
            ready.Put(ImageColumns.IsPending, 0);
            if (resolver.Update(item, ready, null, null) != 1)
            {
                throw new IOException("MediaStore did not publish the captured photo.");
            }

            return ToDocumentUri(item).ToString();
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(item);
            throw;
        }
        catch (Exception ex)
        {
            DeleteQuietly(item);
            logger.LogError(ex, "Failed to save a captured photo to MediaStore.");
            return null;
        }
    }

    public Task<IReadOnlyList<PhotoAsset>> GetCapturedPhotosAsync(int limit,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PhotoAsset>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<PhotoAsset> assets = new List<PhotoAsset>();
            string[] projection =
            {
                ImageColumns.Id,
                ImageColumns.DateTaken,
                ImageColumns.DateAdded
            };
            string selection =
                $"{ImageColumns.RelativePath} = ? AND " +
                $"{ImageColumns.OwnerPackageName} = ? AND " +
                $"{ImageColumns.IsPending} = 0";
            string[] arguments =
            {
                RelativePath,
                context.PackageName ?? string.Empty
            };
            string sortOrder =
                $"{ImageColumns.DateTaken} DESC, " +
                $"{ImageColumns.DateAdded} DESC, " +
                $"{ImageColumns.Id} DESC";

            try
            {
                using global::Android.Database.ICursor? cursor = resolver.Query(
                    CapturedPhotoCollection,
                    projection,
                    selection,
                    arguments,
                    sortOrder);

                if (cursor is null)
                {
                    return assets;
                }

                int idColumn = cursor.GetColumnIndexOrThrow(ImageColumns.Id);
                int takenColumn = cursor.GetColumnIndexOrThrow(ImageColumns.DateTaken);
                int addedColumn = cursor.GetColumnIndexOrThrow(ImageColumns.DateAdded);

                while (cursor.MoveToNext() && (limit <= 0 || assets.Count < limit))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    long mediaId = cursor.GetLong(idColumn);
                    AndroidUri mediaStoreUri = ContentUris.WithAppendedId(
                        CapturedPhotoCollection, mediaId);
                    AndroidUri documentUri = ToDocumentUri(mediaStoreUri);
                    DateTimeOffset? createdAt = ReadMediaStoreDate(cursor, takenColumn, addedColumn);
                    GeoPosition? location = ReadExif(mediaStoreUri).Location;
                    assets.Add(new PhotoAsset(documentUri.ToString()!, createdAt, location));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to query captured photos from MediaStore.");
            }

            return assets;
        }, cancellationToken);

    public Task<IReadOnlyList<PhotoAsset>> GetPhotosAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return Task.Run<IReadOnlyList<PhotoAsset>>(() =>
        {
            List<PhotoAsset> assets = new List<PhotoAsset>(ids.Count);
            foreach (string id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseContentUri(id, out AndroidUri? uri))
                {
                    continue;
                }

                AndroidUri readableUri = GetReadableUri(uri);
                if (!CanOpen(readableUri))
                {
                    continue;
                }

                PhotoExifData exif = ReadExif(readableUri);
                DateTimeOffset? createdAt = exif.TakenAt is DateTime takenAt
                    ? new DateTimeOffset(takenAt)
                    : ReadDateFromProvider(readableUri);
                assets.Add(new PhotoAsset(id, createdAt, exif.Location));
            }

            return assets;
        }, cancellationToken);
    }

    public Task<byte[]?> GetThumbnailAsync(string id,
        int pixelSize,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (pixelSize <= 0 || !TryParseContentUri(id, out AndroidUri? uri))
            {
                return null;
            }

            using CancellationSignal signal = new CancellationSignal();
            using CancellationTokenRegistration registration =
                cancellationToken.Register(static state => ((CancellationSignal)state!).Cancel(), signal);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Bitmap? bitmap = resolver.LoadThumbnail(
                    GetReadableUri(uri),
                    new AndroidSize(pixelSize, pixelSize),
                    signal);
                if (bitmap is null)
                {
                    return null;
                }

                using MemoryStream thumbnail = new MemoryStream();
                return bitmap.Compress(Bitmap.CompressFormat.Jpeg!, ThumbnailQuality, thumbnail)
                    ? thumbnail.ToArray()
                    : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (global::Android.OS.OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to load thumbnail {Id}.", id);
                return null;
            }
        }, cancellationToken);

    public async Task<byte[]?> GetPhotoDataAsync(string id,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseContentUri(id, out AndroidUri? uri))
        {
            return null;
        }

        try
        {
            await using Stream? input = resolver.OpenInputStream(GetReadableUri(uri));
            if (input is null)
            {
                return null;
            }

            using MemoryStream data = new MemoryStream();
            await input.CopyToAsync(data, cancellationToken);
            return data.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read photo {Id}.", id);
            return null;
        }
    }

    public Task<XmpUploadState> ReadUploadStateAsync(string id,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!TryParseContentUri(id, out AndroidUri? uri))
            {
                return XmpUploadState.NotUploaded;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Stream? input = resolver.OpenInputStream(GetReadableUri(uri));
                return input is null
                    ? XmpUploadState.NotUploaded
                    : JpegSegmentScanner.ReadUploadState(input);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to read upload state from {Id}.", id);
                return XmpUploadState.NotUploaded;
            }
        }, cancellationToken);

    public async Task<bool> WriteUploadStateAsync(string id,
        XmpUploadState state,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseContentUri(id, out AndroidUri? uri) || GetCapturedStatus(uri) != CapturedStatus.Captured)
        {
            return false;
        }

        byte[]? original = null;
        bool overwriteStarted = false;
        AndroidUri writableUri = ToMediaStoreUri(uri);
        try
        {
            original = await GetPhotoDataAsync(id, cancellationToken);
            if (original is null)
            {
                return false;
            }

            byte[] updated = await Task.Run(
                () => JpegXmpEditor.SetUploadState(original, state),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            await using Stream? output = resolver.OpenOutputStream(writableUri, "rwt");
            if (output is null)
            {
                return false;
            }

            // Once the provider has truncated the asset, finish without cancellation so a cancelled
            // background upload cannot leave the user's JPEG half-written.
            overwriteStarted = true;
            await output.WriteAsync(updated, CancellationToken.None);
            await output.FlushAsync(CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (overwriteStarted && original is not null)
            {
                await RestoreOriginalAsync(writableUri, original);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (overwriteStarted && original is not null)
            {
                await RestoreOriginalAsync(writableUri, original);
            }

            logger.LogError(ex, "Failed to write upload state to captured photo {Id}.", id);
            return false;
        }
    }

    public async Task<IReadOnlyList<PickedPhoto>> PickPhotosAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<IReadOnlyList<AndroidUri>> result = PhotoLibraryActivityCoordinator.BeginPick();
        if (result.IsCompleted)
        {
            return Array.Empty<PickedPhoto>();
        }

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                global::Android.App.Activity? activity = Platform.CurrentActivity;
                if (activity is null)
                {
                    PhotoLibraryActivityCoordinator.CompletePick(Array.Empty<AndroidUri>());
                    return;
                }

                activity.StartActivity(PhotoLibraryActivity.CreatePickIntent(activity, limit));
            });

            IReadOnlyList<AndroidUri> uris = await result.WaitAsync(cancellationToken);
            return uris
                .Select(uri => new PickedPhoto(uri.ToString(), null))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            PhotoLibraryActivityCoordinator.CompletePick(Array.Empty<AndroidUri>());
            throw;
        }
        catch (Exception ex)
        {
            PhotoLibraryActivityCoordinator.CompletePick(Array.Empty<AndroidUri>());
            logger.LogError(ex, "Failed to show the Android photo picker.");
            return Array.Empty<PickedPhoto>();
        }
    }

    public Task ReleasePhotoAccessAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return Task.Run(() =>
        {
            foreach (string id in ids.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseContentUri(id, out AndroidUri? uri))
                {
                    continue;
                }

                try
                {
                    resolver.ReleasePersistableUriPermission(
                        uri,
                        ActivityFlags.GrantReadUriPermission);
                }
                catch (Java.Lang.SecurityException)
                {
                    // The URI was app-owned or its grant was already gone.
                }
            }
        }, cancellationToken);
    }

    public async Task<bool> DeletePhotosAsync(IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return true;
        }

        List<AndroidUri> existing = new List<AndroidUri>(ids.Count);
        foreach (string id in ids.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseContentUri(id, out AndroidUri? uri))
            {
                return false;
            }

            switch (GetCapturedStatus(uri))
            {
                case CapturedStatus.Captured:
                    existing.Add(uri);
                    break;
                case CapturedStatus.NotCaptured:
                    return false;
            }
        }

        if (existing.Count == 0)
        {
            return true;
        }

        try
        {
            return await Task.Run(() =>
            {
                foreach (AndroidUri uri in existing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    resolver.Delete(ToMediaStoreUri(uri), null, null);
                }

                return true;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete captured photos from MediaStore.");
            return false;
        }
    }

    private PhotoExifData ReadExif(AndroidUri uri)
    {
        try
        {
            using Stream? input = resolver.OpenInputStream(uri);
            if (input is null)
            {
                return PhotoExifData.Empty;
            }

            using global::Android.Media.ExifInterface exif = new global::Android.Media.ExifInterface(input);
            DateTime? takenAt = ReadExifDate(exif);
            float[] coordinates = new float[2];
            GeoPosition? location = exif.GetLatLong(coordinates)
                ? new GeoPosition(coordinates[0], coordinates[1])
                : null;
            return new PhotoExifData(takenAt, location);
        }
        catch (Java.Lang.SecurityException ex)
        {
            logger.LogError(ex, "Android denied EXIF access to {Uri}.", uri);
            return PhotoExifData.Empty;
        }
        catch (Exception ex) when (ex is Java.IO.IOException or IOException)
        {
            logger.LogDebug(ex, "Could not read EXIF from {Uri}.", uri);
            return PhotoExifData.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not parse EXIF from {Uri}.", uri);
            return PhotoExifData.Empty;
        }
    }

    private static DateTime? ReadExifDate(global::Android.Media.ExifInterface exif)
    {
        string? value = exif.GetAttribute(global::Android.Media.ExifInterface.TagDatetimeOriginal)
            ?? exif.GetAttribute(global::Android.Media.ExifInterface.TagDatetimeDigitized)
            ?? exif.GetAttribute(global::Android.Media.ExifInterface.TagDatetime);

        if (DateTime.TryParseExact(value,
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out DateTime takenAt))
        {
            return takenAt;
        }

        return null;
    }

    private DateTimeOffset? ReadDateFromProvider(AndroidUri uri)
    {
        try
        {
            string[] projection =
            {
                ImageColumns.DateTaken,
                ImageColumns.DateAdded
            };
            using global::Android.Database.ICursor? cursor =
                resolver.Query(ToMediaStoreUri(uri), projection, null, null, null);

            if (cursor is null || !cursor.MoveToFirst())
            {
                return null;
            }

            return ReadMediaStoreDate(
                cursor,
                cursor.GetColumnIndex(ImageColumns.DateTaken),
                cursor.GetColumnIndex(ImageColumns.DateAdded));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadMediaStoreDate(global::Android.Database.ICursor cursor,
        int takenColumn,
        int addedColumn)
    {
        if (takenColumn >= 0 && !cursor.IsNull(takenColumn))
        {
            long milliseconds = cursor.GetLong(takenColumn);
            if (milliseconds > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
        }

        if (addedColumn >= 0 && !cursor.IsNull(addedColumn))
        {
            long seconds = cursor.GetLong(addedColumn);
            if (seconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }

    private bool CanOpen(AndroidUri uri)
    {
        try
        {
            using ParcelFileDescriptor? descriptor = resolver.OpenFileDescriptor(uri, "r");
            return descriptor is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private CapturedStatus GetCapturedStatus(AndroidUri uri)
    {
        try
        {
            string[] projection =
            {
                ImageColumns.RelativePath,
                ImageColumns.OwnerPackageName
            };
            using global::Android.Database.ICursor? cursor =
                resolver.Query(ToMediaStoreUri(uri), projection, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
            {
                return CapturedStatus.Missing;
            }

            string? path = cursor.GetString(
                cursor.GetColumnIndexOrThrow(ImageColumns.RelativePath));
            string? owner = cursor.GetString(
                cursor.GetColumnIndexOrThrow(ImageColumns.OwnerPackageName));

            return string.Equals(path, RelativePath, StringComparison.Ordinal)
                && string.Equals(owner, context.PackageName, StringComparison.Ordinal)
                    ? CapturedStatus.Captured
                    : CapturedStatus.NotCaptured;
        }
        catch (Exception)
        {
            return CapturedStatus.NotCaptured;
        }
    }

    private static bool TryParseContentUri(string? id, [NotNullWhen(true)] out AndroidUri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            uri = AndroidUri.Parse(id);
            return uri is not null
                && string.Equals(uri.Scheme, ContentResolver.SchemeContent, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            uri = null;
            return false;
        }
    }

    private AndroidUri GetReadableUri(AndroidUri uri) =>
        GetCapturedStatus(uri) == CapturedStatus.Captured
            ? ToMediaStoreUri(uri)
            : uri;

    private static AndroidUri ToDocumentUri(AndroidUri mediaStoreUri)
    {
        long mediaId = ContentUris.ParseId(mediaStoreUri);
        return DocumentsContract.BuildDocumentUri(
            MediaDocumentsAuthority,
            $"image:{mediaId}")!;
    }

    private static AndroidUri ToMediaStoreUri(AndroidUri uri)
    {
        if (!string.Equals(uri.Authority, MediaDocumentsAuthority, StringComparison.Ordinal) ||
            !DocumentsContract.IsDocumentUri(global::Android.App.Application.Context, uri))
        {
            return uri;
        }

        string? documentId = DocumentsContract.GetDocumentId(uri);
        const string imagePrefix = "image:";
        if (documentId is null ||
            !documentId.StartsWith(imagePrefix, StringComparison.Ordinal) ||
            !long.TryParse(
                documentId.AsSpan(imagePrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long mediaId))
        {
            return uri;
        }

        return ContentUris.WithAppendedId(CapturedPhotoCollection, mediaId);
    }

    private void DeleteQuietly(AndroidUri? uri)
    {
        if (uri is null)
        {
            return;
        }

        try
        {
            resolver.Delete(uri, null, null);
        }
        catch (Exception)
        {
        }
    }

    private async Task RestoreOriginalAsync(AndroidUri uri, byte[] original)
    {
        try
        {
            await using Stream? output = resolver.OpenOutputStream(uri, "rwt");
            if (output is not null)
            {
                await output.WriteAsync(original, CancellationToken.None);
                await output.FlushAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore {Id} after an interrupted metadata update.", uri);
        }
    }

    private enum CapturedStatus
    {
        Missing,
        Captured,
        NotCaptured
    }

    private static class ImageColumns
    {
        internal const string Id = global::Android.Provider.IBaseColumns.Id;
        internal const string DateTaken = MediaStore.Images.IImageColumns.DateTaken;
        internal const string DateAdded = MediaStore.IMediaColumns.DateAdded;
        internal const string DisplayName = MediaStore.IMediaColumns.DisplayName;
        internal const string IsPending = MediaStore.IMediaColumns.IsPending;
        internal const string MimeType = MediaStore.IMediaColumns.MimeType;
        internal const string OwnerPackageName = MediaStore.IMediaColumns.OwnerPackageName;
        internal const string RelativePath = MediaStore.IMediaColumns.RelativePath;
    }
}
