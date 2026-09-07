using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class UploadQueueTests
{
    [Fact]
    public async Task InsertReservationCannotDrainAndFailureRollsBackCredentials()
    {
        Fixture f = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.BeforeAdd = async () => { entered.SetResult(); await release.Task; throw new IOException("disk full"); };
        using UploadQueue queue = f.Queue();
        Task enqueue = queue.EnqueueAsync(f.Photos, f.Draft);
        await entered.Task;
        Task drain = queue.DrainAsync();
        Assert.Empty(queue.Reports);
        Assert.Equal(0, f.Uploads.Preparations);
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => enqueue);
        await drain;
        Assert.Empty(queue.Reports);
        Assert.Equal(0, f.Uploads.Preparations);
        Assert.DoesNotContain("mastodon-token-a", string.Join("", f.Auth.Storage.Values
            .Where(p => p.Key.Contains("queued")).Select(p => p.Value)));
        f.Store.BeforeAdd = null;
        Assert.True(await queue.EnqueueAsync(f.Photos, f.Draft));
        Assert.False(await queue.EnqueueAsync(f.Photos, f.Draft));
    }

    [Fact]
    public async Task LostInsertResponsePublishesTheDurableRowExactlyOnce()
    {
        Fixture f = new();
        f.Store.LoseAddResponse = true;
        using UploadQueue queue = f.Queue();
        Assert.True(await queue.EnqueueAsync(f.Photos, f.Draft));
        Assert.Single(queue.Reports);
        await queue.DrainAsync();
        Assert.Equal(1, f.Uploads.Finalizations);
        Assert.Empty(queue.Reports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AcceptedReportReplaysOnlyLocalAcknowledgementAfterRestart(int acknowledged)
    {
        Fixture f = new();
        f.Catalog.FailAfterMarks = acknowledged;
        using (UploadQueue first = f.Queue())
        {
            await first.EnqueueAsync(f.Photos, f.Draft);
            await first.DrainAsync();
            Assert.NotNull(Assert.Single(first.Reports).Receipt);
        }
        f.Catalog.FailAfterMarks = null;
        f.Runtime.Now = f.Runtime.Now.AddHours(1);
        // No secure credentials are needed to finish XMP/index acknowledgement.
        f.Auth.Storage.Fail = true;
        using UploadQueue reopened = f.Queue();
        await reopened.DrainAsync();
        Assert.Empty(reopened.Reports);
        Assert.Equal(1, f.Uploads.Preparations);
        Assert.Equal(1, f.Uploads.Finalizations);
        Assert.Equal(4, f.Catalog.Marked.Count);
        Assert.All(f.Catalog.Marked.Values, value =>
        {
            Assert.Equal(f.Uploads.Receipt!.SubmissionId, value.SubmissionId);
            Assert.Equal(f.Uploads.Receipt.SubmittedAt, value.SubmittedAt);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainNetworkOrReceiptPersistenceReconcilesWithoutResubmission(bool diskFailure)
    {
        Fixture f = new();
        f.Uploads.AfterAccept = () =>
        {
            if (diskFailure) f.Store.FailUpdates = true;
            else throw new HttpRequestException("response lost");
        };
        using (UploadQueue first = f.Queue())
        {
            await first.EnqueueAsync(f.Photos, f.Draft);
            if (diskFailure) await Assert.ThrowsAsync<IOException>(() => first.DrainAsync());
            else await first.DrainAsync();
        }
        f.Store.FailUpdates = false;
        f.Runtime.Now = f.Runtime.Now.AddHours(1);
        using UploadQueue reopened = f.Queue();
        await reopened.DrainAsync();
        Assert.Empty(reopened.Reports);
        Assert.Equal(1, f.Uploads.Preparations);
        Assert.Equal(1, f.Uploads.Finalizations);
        Assert.Equal(1, f.Uploads.StatusReads);
    }

    [Fact]
    public async Task SignOutAndSwitchCannotReplaceQueuedCredentials()
    {
        Fixture f = new();
        using UploadQueue queue = f.Queue();
        await queue.EnqueueAsync(f.Photos, f.Draft);
        await f.Active.SignOutBlueskyAsync();
        await f.Active.SignOutMastodonAsync();
        f.Auth.Storage.Values["cbl.active-session.v2"] = JsonSerializer.Serialize(new AccountSession(
            new AccountCredential("did:plc:b", "b.bsky.social", "token-b")));
        await queue.DrainAsync();
        Assert.Equal("token-a", f.Uploads.Credentials?.Bluesky?.Token);
        Assert.Equal("mastodon-token-a", f.Uploads.Credentials?.Mastodon?.Token);
        Assert.Equal("did:plc:a", f.Uploads.Receipt?.Attribution.BlueskyDid);
        Assert.DoesNotContain("token-a", string.Join("", f.Store.Rows.Values));
        Assert.DoesNotContain("mastodon-token-a", string.Join("", f.Auth.Storage.Values
            .Where(p => p.Key.Contains("queued")).Select(p => p.Value)));
    }

    [Fact]
    public async Task ExpiredOrRevokedCredentialsAutomaticallyFallBackToWholeReportAnonymous()
    {
        Fixture f = new();
        f.Uploads.RejectCredentials = true;
        using UploadQueue queue = f.Queue();
        await queue.EnqueueAsync(f.Photos, f.Draft);
        await queue.DrainAsync();
        Assert.Equal(2, f.Uploads.Finalizations);
        Assert.Null(f.Uploads.Credentials);
        Assert.True(f.Uploads.Receipt?.Attribution.IsAnonymous);
        Assert.Empty(queue.Reports);
        Assert.All(f.Catalog.Marked.Values, receipt => Assert.True(receipt.Attribution.IsAnonymous));
    }

    [Fact]
    public async Task ProviderUnavailabilityDoesNotFallBackAndStatusFailureNeverMeansNotFound()
    {
        Fixture f = new();
        f.Uploads.Unavailable = true;
        using UploadQueue queue = f.Queue();
        await queue.EnqueueAsync(f.Photos, f.Draft);
        await queue.DrainAsync();
        QueuedReport report = Assert.Single(queue.Reports);
        Assert.False(report.AnonymousFallback);
        Assert.False(report.CanDiscard);
        f.Uploads.StatusUnavailable = true;
        f.Runtime.Now = f.Runtime.Now.AddHours(1);
        await queue.DrainAsync();
        Assert.Equal(1, f.Uploads.Finalizations);
        Assert.Equal(1, f.Uploads.Preparations);
        Assert.False(Assert.Single(queue.Reports).AnonymousFallback);
    }

    [Fact]
    public async Task MultipleReportsShareAndReleaseSecureReferencesIndependently()
    {
        TestSecureStorage storage = new();
        QueuedCredentialVault vault = new(storage);
        AccountSession session = new(new AccountCredential("a", "A", "secret"));
        string first = await vault.RetainAsync("one", session);
        string second = await vault.RetainAsync("two", session);
        Assert.Equal(first, second);
        await new QueuedCredentialVault(storage).ReleaseAsync("one");
        Assert.Equal(session, await new QueuedCredentialVault(storage).ResolveAsync("two", second));
        await vault.ReconcileAsync(new HashSet<string>());
        await Assert.ThrowsAsync<IOException>(() => vault.ResolveAsync("two", second));
    }

    [Fact]
    public async Task RetirementFailureNeverResubmitsAndRetryCannotResetReceipt()
    {
        Fixture f = new();
        f.Store.FailRemove = true;
        using UploadQueue queue = f.Queue();
        await queue.EnqueueAsync(f.Photos, f.Draft);
        await queue.DrainAsync();
        QueuedReport report = Assert.Single(queue.Reports);
        Assert.NotNull(report.Receipt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DiscardAsync(report.Id));
        f.Store.FailRemove = false;
        f.Runtime.Now = f.Runtime.Now.AddHours(1);
        await queue.DrainAsync();
        Assert.Empty(queue.Reports);
        Assert.Equal(1, f.Uploads.Finalizations);
    }

    [Fact]
    public async Task FailedInsertReloadWaitsForLiveDrainWithoutReplacingItsReport()
    {
        Fixture f = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Uploads.BeforeFinalize = async () => { entered.TrySetResult(); await release.Task; };
        using UploadQueue queue = f.Queue();
        await queue.EnqueueAsync(f.Photos, f.Draft);
        Task sending = queue.DrainAsync();
        await entered.Task;
        f.Store.BeforeAdd = () =>
        {
            f.Store.FailReads = true;
            throw new IOException("insert and reconciliation failed");
        };
        await Assert.ThrowsAsync<IOException>(() => queue.EnqueueAsync(
            [new ReportPhoto { Id = "another", Origin = SeattleCarsInBikeLanes.Mobile.Core.Photos.PhotoOrigin.Captured }], f.Draft));
        f.Store.FailReads = false;
        Task restart = queue.StartAsync();
        Assert.False(restart.IsCompleted);
        release.SetResult();
        await sending;
        await restart;
        Assert.Empty(queue.Reports);
        Assert.Empty(f.Store.Rows);
        await queue.DrainAsync();
    }

    private sealed class Fixture
    {
        public AuthFixture Auth = new();
        public AuthService Active;
        public QueueStore Store = new();
        public Uploader Uploads = new();
        public Catalog Catalog = new();
        public Runtime Runtime = new();
        public ReportDraft Draft = new()
        {
            Attribute = true,
            NumberOfCars = 1,
            TakenAt = DateTime.Now,
            Location = new GeoPosition(47.6062, -122.3321)
        };
        public ReportPhoto[] Photos = Enumerable.Range(0, 4).Select(i =>
            new ReportPhoto { Id = $"p{i}", Origin = SeattleCarsInBikeLanes.Mobile.Core.Photos.PhotoOrigin.Captured }).ToArray();
        public Fixture() { Active = Auth.Create(); }
        public UploadQueue Queue() => new(Store, Uploads, Catalog, Active,
            new QueuedCredentialVault(Auth.Storage), new NullBackgroundWorkScope(), Runtime,
            NullLogger<UploadQueue>.Instance);
    }

    private sealed class Runtime : IQueueRuntime
    {
        public DateTime Now = DateTime.UtcNow;
        public DateTime UtcNow => Now;
        public event EventHandler? ConnectivityChanged { add { } remove { } }
        public void Dispatch(Action action) => action();
        public void Run(Func<Task> action) { }
    }

    private sealed class QueueStore : IUploadQueueStore
    {
        public Dictionary<string, string> Rows = [];
        public Func<Task>? BeforeAdd;
        public bool LoseAddResponse, FailUpdates, FailRemove, FailReads;
        public Task<IReadOnlyList<UploadQueueRecord>> GetAllAsync() =>
            FailReads ? throw new IOException("read failed") :
                Task.FromResult<IReadOnlyList<UploadQueueRecord>>(Rows.Values.Select(v => JsonSerializer.Deserialize<UploadQueueRecord>(v)!).ToArray());
        public async Task AddAsync(UploadQueueRecord record)
        {
            if (BeforeAdd is not null) await BeforeAdd();
            Rows.Add(record.Id, JsonSerializer.Serialize(record));
            if (LoseAddResponse) throw new IOException("insert response lost");
        }
        public Task UpdateAsync(UploadQueueRecord record)
        {
            if (FailUpdates) throw new IOException("disk full");
            if (!Rows.ContainsKey(record.Id)) throw new IOException("missing row");
            Rows[record.Id] = JsonSerializer.Serialize(record);
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string id)
        {
            if (FailRemove) throw new IOException("disk unavailable");
            Rows.Remove(id); return Task.CompletedTask;
        }
        public async Task ResetInterruptedAsync()
        {
            foreach (UploadQueueRecord record in await GetAllAsync())
                if (record.State == (int)UploadQueueState.Uploading)
                { record.State = (int)UploadQueueState.Pending; await UpdateAsync(record); }
        }
    }

    private sealed class Uploader : IUploadService
    {
        public UploadLimits Limits { get; } = new();
        public BoundingBox BoundingBox => BoundingBox.Seattle;
        public int Preparations, Finalizations, StatusReads;
        public bool RejectCredentials, Unavailable, StatusUnavailable;
        public Action? AfterAccept;
        public Func<Task>? BeforeFinalize;
        public AccountSession? Credentials;
        public SubmissionReceipt? Receipt;
        public Task RefreshLimitsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UploadPreparation> PrepareAsync(IReadOnlyList<UploadPhoto> photos, CancellationToken cancellationToken = default)
        {
            Preparations++; return Task.FromResult(new UploadPreparation(photos.Select((p, i) =>
            new InitialPhotoUpload { PhotoId = $"attempt-{Preparations}-{i}", PhotoNumber = i, SubmissionId = $"attempt-{Preparations}" }).ToArray()));
        }
        public Task<SubmissionReceipt?> GetReceiptAsync(string reportId, CancellationToken cancellationToken = default)
        { StatusReads++; if (StatusUnavailable) throw new HttpRequestException("offline"); return Task.FromResult(Receipt); }
        public async Task<SubmissionReceipt> FinalizeAsync(UploadPreparation preparation, ReportDraft draft,
            QueuedAttribution attribution, AccountSession? credentials, string reportId, CancellationToken cancellationToken = default)
        {
            Finalizations++; Credentials = credentials;
            if (BeforeFinalize is not null) await BeforeFinalize();
            if (RejectCredentials && credentials is not null) throw new QueuedCredentialRejectedException();
            if (Unavailable) throw new UploadException("provider unavailable", HttpStatusCode.ServiceUnavailable);
            Receipt ??= new SubmissionReceipt(reportId, reportId, DateTimeOffset.UtcNow, attribution.Intent);
            AfterAccept?.Invoke();
            return Receipt;
        }
    }

    private sealed class Catalog : IPhotoCatalog
    {
        public Dictionary<string, SubmissionReceipt> Marked = [];
        public int? FailAfterMarks;
        public Task<byte[]?> GetPhotoDataAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>([1, 2, 3]);
        public Task MarkSubmittedAsync(IReadOnlyList<ReportPhoto> photos, string? submissionId,
            CancellationToken cancellationToken = default, DateTimeOffset? submittedAt = null)
        {
            foreach (ReportPhoto photo in photos)
            {
                if (FailAfterMarks.HasValue && Marked.Count >= FailAfterMarks.Value) throw new IOException("XMP write interrupted");
                // Receipt attribution is asserted separately by the uploader; photo XMP stores ID/time.
                Marked[photo.Id] = new SubmissionReceipt(submissionId!, submissionId!, submittedAt!.Value, new ReportAttribution());
            }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ReportPhoto>> GetPhotosAsync(int capturedLimit = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ReportPhoto?> AddCapturedPhotoAsync(byte[] jpeg, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> GetThumbnailAsync(string id, int pixelSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReportPhoto>> ImportPhotosAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ForgetImportedPhotosResult> ForgetImportedPhotosAsync(IReadOnlySet<string> retainedIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ForgetAsync(IReadOnlyList<ReportPhoto> photos, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReportPhoto>> DeleteAsync(IReadOnlyList<ReportPhoto> photos, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
