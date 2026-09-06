using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;

namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

public sealed record PrivatePhotoAsset(
    string Id,
    bool Imported,
    DateTimeOffset? CreatedAt,
    GeoPosition? Location);

public interface IPrivatePhotoContent
{
    PhotoExifData ReadExif(byte[] jpeg);

    XmpUploadState ReadUploadState(byte[] jpeg);

    byte[] SetUploadState(byte[] jpeg, XmpUploadState state);
}

public interface IPrivatePhotoStore
{
    Task<IReadOnlyList<PrivatePhotoAsset>> GetPhotosAsync(CancellationToken cancellationToken = default);

    Task<PrivatePhotoAsset> SaveAsync(byte[] jpeg,
        bool imported,
        CancellationToken cancellationToken = default);

    Task<byte[]?> GetDataAsync(string id, CancellationToken cancellationToken = default);

    Task<XmpUploadState> ReadUploadStateAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> WriteUploadStateAsync(string id,
        XmpUploadState state,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable photos that only this app can see.
/// </summary>
/// <remarks>
/// Files under the app data directory survive process termination, device restarts, and app
/// upgrades. The operating system removes them when the app is uninstalled.
/// </remarks>
public sealed class PrivatePhotoStore : IPrivatePhotoStore
{
    private const string IdPrefix = "private:";
    private const string CapturedDirectoryName = "captured";
    private const string ImportedDirectoryName = "imported";

    private readonly string root;
    private readonly IPrivatePhotoContent content;
    private readonly SemaphoreSlim writes = new SemaphoreSlim(1, 1);

    public PrivatePhotoStore(string root, IPrivatePhotoContent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(content);
        this.root = root;
        this.content = content;
    }

    public Task<IReadOnlyList<PrivatePhotoAsset>> GetPhotosAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PrivatePhotoAsset>>(() =>
        {
            List<PrivatePhotoAsset> photos = new List<PrivatePhotoAsset>();
            ReadDirectory(CapturedDirectoryName, photos, cancellationToken);
            ReadDirectory(ImportedDirectoryName, photos, cancellationToken);
            return photos
                .OrderByDescending(photo => photo.CreatedAt ?? DateTimeOffset.MinValue)
                .ToList();
        }, cancellationToken);

    public async Task<PrivatePhotoAsset> SaveAsync(byte[] jpeg,
        bool imported,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length == 0)
        {
            throw new ArgumentException("A photo cannot be empty.", nameof(jpeg));
        }

        string directoryName = imported ? ImportedDirectoryName : CapturedDirectoryName;
        string directory = Path.Combine(root, directoryName);
        string fileName = $"{Guid.NewGuid():N}.jpg";
        string path = Path.Combine(directory, fileName);
        string temporaryPath = path + ".tmp";

        await writes.WaitAsync(cancellationToken);
        try
        {
            DurableFile.CreateDirectory(directory);
            await using (FileStream output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(jpeg, cancellationToken);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path);
            DurableFile.SyncDirectory(directory);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            writes.Release();
        }

        return ReadAsset(directoryName, path, cancellationToken);
    }

    public async Task<byte[]?> GetDataAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!TryResolve(id, out _, out string? path) || !File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    public async Task<XmpUploadState> ReadUploadStateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        byte[]? jpeg = await GetDataAsync(id, cancellationToken);
        return jpeg is null ? XmpUploadState.NotUploaded : content.ReadUploadState(jpeg);
    }

    public async Task<bool> WriteUploadStateAsync(string id,
        XmpUploadState state,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolve(id, out _, out string? path) || !File.Exists(path))
        {
            return false;
        }

        await writes.WaitAsync(cancellationToken);
        string temporaryPath = path + ".tmp";
        try
        {
            byte[] original = await File.ReadAllBytesAsync(path, cancellationToken);
            byte[] updated = content.SetUploadState(original, state);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            await DurableFile.WriteAsync(temporaryPath, updated, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
            DurableFile.SyncDirectory(Path.GetDirectoryName(path)!);
            byte[] persisted = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!persisted.AsSpan().SequenceEqual(updated))
            {
                throw new IOException("The private photo metadata could not be verified.");
            }
            return true;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            writes.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!TryResolve(id, out _, out string? path))
        {
            return false;
        }

        await writes.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        finally
        {
            writes.Release();
        }
    }

    private void ReadDirectory(
        string directoryName,
        List<PrivatePhotoAsset> photos,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(root, directoryName);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            photos.Add(ReadAsset(directoryName, path, cancellationToken));
        }
    }

    private PrivatePhotoAsset ReadAsset(
        string directoryName,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] jpeg = File.ReadAllBytes(path);
        PhotoExifData exif = content.ReadExif(jpeg);
        DateTimeOffset? createdAt = exif.TakenAt is DateTime takenAt
            ? new DateTimeOffset(takenAt)
            : File.GetCreationTime(path);
        bool imported = string.Equals(
            directoryName,
            ImportedDirectoryName,
            StringComparison.Ordinal);
        string id = ToId(directoryName, Path.GetFileName(path));
        return new PrivatePhotoAsset(id, imported, createdAt, exif.Location);
    }

    private bool TryResolve(string id, out bool imported, out string? path)
    {
        imported = false;
        path = null;
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = id[IdPrefix.Length..].Split(':');
        string suppliedFileName = parts.Length == 2 ? parts[1] : string.Empty;
        if (parts.Length != 2 ||
            (parts[0] != CapturedDirectoryName && parts[0] != ImportedDirectoryName) ||
            !string.Equals(
                Path.GetFileName(suppliedFileName),
                suppliedFileName,
                StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(suppliedFileName), ".jpg", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(suppliedFileName), "N", out Guid photoId))
        {
            return false;
        }

        imported = parts[0] == ImportedDirectoryName;
        path = Path.Combine(root, parts[0], $"{photoId:N}.jpg");
        return true;
    }

    private static string ToId(string directoryName, string fileName) =>
        $"{IdPrefix}{directoryName}:{fileName}";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup must not hide the original failure.
        }
    }
}
