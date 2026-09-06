using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class PhotoMutationRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"photo-recovery-{Guid.NewGuid():N}");
    private static readonly byte[] Original = [1, 2, 3, 4];
    private static readonly byte[] Updated = [1, 9, 2, 3, 4];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task InterruptedMutationRecoversWithRecreatedServices(int written)
    {
        Target target = new Target { FailAfter = written };
        target.BeforeWrite = () =>
        {
            string operation = Assert.Single(Directory.GetDirectories(root));
            Assert.Equal(Original, File.ReadAllBytes(Path.Combine(operation, "original")));
            Assert.Equal(Updated, File.ReadAllBytes(Path.Combine(operation, "updated")));
            Assert.True(File.Exists(Path.Combine(operation, "journal")));
        };
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(root, target)
            .WriteAsync("photo", _ => Updated, default));

        Target reopenedTarget = new Target(target) { FailAfter = null };
        byte[] read = await new PhotoMutationRecovery(root, reopenedTarget)
            .WithRecoveredAccessAsync(() => reopenedTarget.ReadAsync("photo", default), default);
        Assert.Equal(Updated, read);
        Assert.Empty(Directory.GetDirectories(root));
        Assert.Equal("same-asset", await reopenedTarget.GetIdentityAsync("photo", default));
    }

    [Fact]
    public async Task RecoveryCanItselfBeInterruptedAndRetried()
    {
        Target target = new Target { FailAfter = 0 };
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(root, target)
            .WriteAsync("photo", _ => Updated, default));
        target.FailAfter = 2;
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(root, target)
            .WithRecoveredAccessAsync(() => Task.FromResult(true), default, "photo"));
        target.FailAfter = null;
        await new PhotoMutationRecovery(root, target).WithRecoveredAccessAsync(() => Task.FromResult(true), default);
        Assert.Equal(Updated, target.Bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExternalEditOrIdentityChangeBlocksReadsAndRetainsBackup(bool changeIdentity)
    {
        Target target = new Target { FailAfter = 1 };
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(root, target)
            .WriteAsync("photo", _ => Updated, default));
        if (changeIdentity) target.Identity = "reused-uri";
        else target.Bytes = [8, 8, 8];
        bool accessed = false;
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(root, target)
            .WithRecoveredAccessAsync(() => { accessed = true; return Task.FromResult(true); }, default, "photo"));
        Assert.False(accessed);
        string operation = Assert.Single(Directory.GetDirectories(root));
        Assert.Equal(Original, File.ReadAllBytes(Path.Combine(operation, "original")));
    }

    [Fact]
    public async Task FailedBackupPublicationNeverTruncatesTarget()
    {
        Directory.CreateDirectory(root);
        string blockedRoot = Path.Combine(root, "file");
        await File.WriteAllTextAsync(blockedRoot, "not a directory");
        Target target = new Target();
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(blockedRoot, target)
            .WriteAsync("photo", _ => Updated, default));
        Assert.Equal(Original, target.Bytes);
        Assert.Equal(0, target.Writes);
    }

    [Fact]
    public async Task ConcurrentAccessCannotObserveTruncatedBytes()
    {
        Target target = new Target();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.WriteGate = async () => { entered.SetResult(); await release.Task; };
        PhotoMutationRecovery recovery = new PhotoMutationRecovery(root, target);
        Task write = recovery.WriteAsync("photo", _ => Updated, default);
        await entered.Task;
        Task<byte[]> read = recovery.WithRecoveredAccessAsync(() => target.ReadAsync("photo", default), default);
        Assert.False(read.IsCompleted);
        release.SetResult();
        await write;
        Assert.Equal(Updated, await read);
    }

    [Fact]
    public async Task MatchingCachedBytesMustBeSynchronizedBeforeBackupsAreRetired()
    {
        Target target = new() { FailAfter = Updated.Length };
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(root, target)
            .WriteAsync("photo", _ => Updated, default));
        target.FailAfter = null;
        target.FailSync = true;
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(root, target)
            .WithRecoveredAccessAsync(() => Task.FromResult(true), default, "photo"));
        Assert.Single(Directory.GetDirectories(root));
        target.FailSync = false;
        await new PhotoMutationRecovery(root, target).WithRecoveredAccessAsync(() => Task.FromResult(true), default, "photo");
        Assert.Empty(Directory.GetDirectories(root));
        Assert.True(target.Syncs >= 2);
    }

    [Fact]
    public async Task UnrecoverableTargetIsQuarantinedWithoutBlockingOtherAssets()
    {
        Target target = new() { FailAfter = 0 };
        await Assert.ThrowsAsync<IOException>(() => new PhotoMutationRecovery(root, target)
            .WriteAsync("photo", _ => Updated, default));
        target.Identity = "missing-or-reused-target";
        PhotoMutationRecovery reopened = new(root, target);
        Assert.True(await reopened.WithRecoveredAccessAsync(() => Task.FromResult(true), default, "unrelated"));
        Assert.True(reopened.IsBlocked("photo"));
        Assert.Single(Directory.GetDirectories(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Target : IRecoverablePhotoTarget
    {
        public byte[] Bytes = Original.ToArray();
        public string Identity = "same-asset";
        public int? FailAfter;
        public int Writes;
        public int Syncs;
        public bool FailSync;
        public Action? BeforeWrite;
        public Func<Task>? WriteGate;
        public Target() { }
        public Target(Target previous) { Bytes = previous.Bytes.ToArray(); Identity = previous.Identity; }
        public Task<string> GetIdentityAsync(string id, CancellationToken token) => Task.FromResult(Identity);
        public Task<byte[]> ReadAsync(string id, CancellationToken token) => Task.FromResult(Bytes.ToArray());
        public Task SynchronizeAsync(string id, CancellationToken token)
        {
            Syncs++;
            if (FailSync) throw new IOException("sync failed");
            return Task.CompletedTask;
        }
        public async Task WriteAndSyncAsync(string id, byte[] bytes, CancellationToken token)
        {
            BeforeWrite?.Invoke();
            Bytes = [];
            Writes++;
            if (WriteGate is not null) await WriteGate();
            Bytes = bytes.Take(FailAfter ?? bytes.Length).ToArray();
            if (FailAfter.HasValue) throw new IOException("Process terminated during photo write.");
        }
    }
}
