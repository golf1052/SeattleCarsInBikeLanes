using SeattleCarsInBikeLanes.Mobile.Core.Navigation;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class WebAuthActionStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"web-actions-{Guid.NewGuid():N}");

    [Fact]
    public void ReplacementsAndAcknowledgementsSurviveStoreRecreation()
    {
        string path = Path.Combine(root, "nested", "actions.json");
        WebAuthAction first = new(1, WebAuthActionKind.ApplySignedOut, WebAuthProvider.Bluesky);
        WebAuthAction second = new(2, WebAuthActionKind.ApplySignedOut, WebAuthProvider.Mastodon);
        WebAuthActionStore store = new(path);

        Assert.Empty(store.Read());
        store.Write([first, second]);
        Assert.Equal([first, second], new WebAuthActionStore(path).Read());
        store.Write([second]);
        Assert.Equal([second], new WebAuthActionStore(path).Read());
        store.Write([]);
        Assert.Empty(new WebAuthActionStore(path).Read());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void UnpublishedTemporaryFileIsIgnoredAndReplacedOnRetry()
    {
        string path = Path.Combine(root, "actions.json");
        WebAuthAction original = new(1, WebAuthActionKind.ApplySignedOut, WebAuthProvider.Bluesky);
        WebAuthAction updated = original with { Id = 2 };
        new WebAuthActionStore(path).Write([original]);
        File.WriteAllText(path + ".tmp", "[");

        WebAuthActionStore reopened = new(path);
        Assert.Equal([original], reopened.Read());
        reopened.Write([updated]);

        Assert.Equal([updated], new WebAuthActionStore(path).Read());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void TemporaryWriteFailurePropagatesWithoutReplacingPendingActions()
    {
        string path = Path.Combine(root, "actions.json");
        WebAuthAction action = new(1, WebAuthActionKind.ApplySignedOut, WebAuthProvider.Bluesky);
        WebAuthActionStore store = new(path);
        store.Write([action]);
        using (FileStream locked = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.Write([]));
            Assert.Equal([action], new WebAuthActionStore(path).Read());
        }

        store.Write([]);
        Assert.Empty(new WebAuthActionStore(path).Read());
    }

    [Fact]
    public void ReplacementFailurePropagatesAndCanBeRetried()
    {
        string path = Path.Combine(root, "actions.json");
        Directory.CreateDirectory(path);
        WebAuthAction action = new(1, WebAuthActionKind.ApplySignedOut, WebAuthProvider.Bluesky);
        WebAuthActionStore store = new(path);

        Assert.Throws<IOException>(() => store.Write([action]));
        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(path + ".tmp"));

        Directory.Delete(path);
        store.Write([action]);
        Assert.Equal([action], new WebAuthActionStore(path).Read());
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
