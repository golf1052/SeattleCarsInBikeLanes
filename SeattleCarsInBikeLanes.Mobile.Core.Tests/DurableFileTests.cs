using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class DurableFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"file-contents-{Guid.NewGuid():N}");

    [Fact]
    public async Task WrittenContentsCanBeReadAfterStreamCloses()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "file");
        byte[] expected = [1, 2, 3, 4];

        await DurableFile.WriteAsync(path, expected);

        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ExistingFileIsNotOverwritten()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "file");
        byte[] original = [1, 2, 3, 4];
        await DurableFile.WriteAsync(path, original);

        await Assert.ThrowsAsync<IOException>(() => DurableFile.WriteAsync(path, [9]));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MissingDirectoryFailurePropagates()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            DurableFile.WriteAsync(Path.Combine(root, "file"), [1]));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task WriteCancellationPropagates()
    {
        Directory.CreateDirectory(root);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DurableFile.WriteAsync(Path.Combine(root, "file"), [1], cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
