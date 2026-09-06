using Microsoft.Extensions.Logging;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Services;

public sealed class QueuedReport
{
    public required string Id { get; init; }
    public required IReadOnlyList<ReportPhoto> Photos { get; init; }
    public required ReportDraft Draft { get; init; }
    public DateTime CreatedAt { get; init; }
    public UploadQueueState State { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public bool ServerDirectedRetry { get; set; }
    public QueuedAttribution Attribution { get; set; } = new QueuedAttribution(new ReportAttribution());
    public SubmissionReceipt? Receipt { get; set; }
    public bool NetworkAttempted { get; set; }
    public bool AnonymousFallback { get; set; }
    public bool CanDiscard => State == UploadQueueState.Failed && !NetworkAttempted && Receipt is null;
    public string Description => Receipt is not null ? "Sent; saving photo status" :
        Photos.Count == 1 ? "1 photo" : $"{Photos.Count} photos";
}

public sealed class QueuedReportCompletedEventArgs(QueuedReport report, string? submissionId) : EventArgs
{
    public QueuedReport Report { get; } = report;
    public string? SubmissionId { get; } = submissionId;
}

public interface IUploadQueue
{
    IReadOnlyList<QueuedReport> Reports { get; }
    event EventHandler? Changed;
    event EventHandler<QueuedReportCompletedEventArgs>? Completed;
    Task StartAsync();
    Task<bool> EnqueueAsync(IReadOnlyList<ReportPhoto> photos, ReportDraft draft,
        CancellationToken cancellationToken = default, long? attributionGeneration = null);
    UploadQueueState? GetPhotoState(string photoId);
    Task RetryAsync(string id);
    Task DiscardAsync(string id);
    void Kick();
    Task DrainAsync(CancellationToken cancellationToken = default);
}

public sealed class UploadQueue : IUploadQueue, IDisposable
{
    private readonly IUploadQueueStore store;
    private readonly IUploadService uploads;
    private readonly IPhotoCatalog catalog;
    private readonly IAuthService auth;
    private readonly QueuedCredentialVault vault;
    private readonly IBackgroundWorkScope background;
    private readonly IQueueRuntime runtime;
    private readonly ILogger<UploadQueue> logger;
    private readonly object gate = new object();
    private readonly SemaphoreSlim mutations = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim drain = new SemaphoreSlim(1, 1);
    private readonly List<QueuedReport> reports = [];
    private readonly Timer timer;
    private bool started;

    public UploadQueue(IUploadQueueStore store, IUploadService uploads, IPhotoCatalog catalog,
        IAuthService auth, QueuedCredentialVault vault, IBackgroundWorkScope background,
        IQueueRuntime runtime, ILogger<UploadQueue> logger)
    {
        this.store = store; this.uploads = uploads; this.catalog = catalog; this.auth = auth;
        this.vault = vault; this.background = background; this.runtime = runtime; this.logger = logger;
        timer = new Timer(_ => Kick(), null, Timeout.Infinite, Timeout.Infinite);
        runtime.ConnectivityChanged += ConnectivityChanged;
    }

    public IReadOnlyList<QueuedReport> Reports { get { lock (gate) return reports.ToArray(); } }
    public event EventHandler? Changed;
    public event EventHandler<QueuedReportCompletedEventArgs>? Completed;

    private async Task InitializeAsync()
    {
        if (Volatile.Read(ref started)) return;
        await drain.WaitAsync();
        try
        {
            await mutations.WaitAsync();
            try
            {
                if (started) return;
                await store.ResetInterruptedAsync();
                IReadOnlyList<UploadQueueRecord> records = await store.GetAllAsync();
                List<QueuedReport> restored = records.Select(Read).ToList();
                // The secure vault is its own durable cleanup registry. Never sweep after a failed load.
                await CleanupCredentialsAsync(() => vault.ReconcileAsync(restored.Where(r => r.Receipt is null && !r.AnonymousFallback &&
                        r.Attribution.CredentialReference is not null).Select(r => r.Id).ToHashSet(StringComparer.Ordinal)));
                lock (gate) { reports.Clear(); reports.AddRange(restored); }
                started = true;
            }
            finally { mutations.Release(); }
        }
        finally { drain.Release(); }
    }

    public async Task StartAsync()
    {
        await InitializeAsync();
        RaiseChanged();
        Kick();
    }

    public async Task<bool> EnqueueAsync(IReadOnlyList<ReportPhoto> photos, ReportDraft draft,
        CancellationToken cancellationToken = default, long? attributionGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(draft);
        await InitializeAsync();
        if (draft.Attribute) await auth.InitializeAsync();
        long generation = attributionGeneration ?? auth.Generation;
        await mutations.WaitAsync(cancellationToken);
        try
        {
            if (!started) throw new IOException("The saved queue must be reloaded before accepting another report.");
            if (photos.Count == 0 || photos.Any(photo => photo.Submitted) ||
                photos.Select(photo => photo.Id).Distinct().Count() != photos.Count ||
                Reports.SelectMany(report => report.Photos).Any(queued => photos.Any(photo => photo.Id == queued.Id)))
                return false;
            QueuedReport report = new QueuedReport
            {
                Id = Guid.NewGuid().ToString("N"),
                Photos = photos.ToArray(),
                Draft = draft.Clone(),
                CreatedAt = runtime.UtcNow,
                State = UploadQueueState.Pending
            };
            await auth.CaptureQueuedAsync(report.Id, draft.Attribute, generation, async attribution =>
            {
                report.Attribution = attribution;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await store.AddAsync(Record(report));
                }
                catch
                {
                    // An insert can commit before its completion is lost. Reconcile rather than
                    // releasing credentials for a durable row or publishing a ghost on failure.
                    UploadQueueRecord? saved;
                    try { saved = (await store.GetAllAsync()).SingleOrDefault(row => row.Id == report.Id); }
                    catch { started = false; throw; }
                    if (saved is null)
                    {
                        await CleanupCredentialsAsync(() => vault.ReleaseAsync(report.Id));
                        throw;
                    }
                    QueuedReport persisted = Read(saved);
                    if (persisted.Attribution != report.Attribution)
                        throw new InvalidDataException("The queued credential reference did not persist.");
                }
            }, cancellationToken);
            lock (gate) reports.Add(report);
        }
        finally { mutations.Release(); }
        RaiseChanged();
        Kick();
        return true;
    }

    public UploadQueueState? GetPhotoState(string id) =>
        Reports.FirstOrDefault(report => report.Photos.Any(photo => photo.Id == id))?.State;

    public async Task RetryAsync(string id)
    {
        await InitializeAsync();
        await mutations.WaitAsync();
        try
        {
            QueuedReport? report = Reports.FirstOrDefault(r => r.Id == id);
            if (report is null || report.State != UploadQueueState.Failed) return;
            report.State = UploadQueueState.Pending;
            report.Attempts = 0;
            report.LastError = null;
            report.NextAttemptAt = null;
            report.ServerDirectedRetry = false;
            await SaveAsync(report);
        }
        finally { mutations.Release(); }
        RaiseChanged();
        Kick();
    }

    public async Task DiscardAsync(string id)
    {
        await InitializeAsync();
        await mutations.WaitAsync();
        try
        {
            QueuedReport? report = Reports.FirstOrDefault(r => r.Id == id);
            if (report is null) return;
            if (!report.CanDiscard)
                throw new InvalidOperationException("A sent or uncertain report must finish saving its photo status.");
            await store.RemoveAsync(id);
            lock (gate) reports.Remove(report);
            await CleanupCredentialsAsync(() => vault.ReleaseAsync(id));
        }
        finally { mutations.Release(); }
        RaiseChanged();
    }

    public void Kick() => runtime.Run(async () =>
    {
        try { await DrainAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "The upload queue stopped; its durable records were retained."); }
    });

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        // Deliberately retain existing non-joining drain ownership; F11 is outside this change.
        if (!await drain.WaitAsync(0, cancellationToken)) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                QueuedReport? report;
                await mutations.WaitAsync(cancellationToken);
                try
                {
                    report = Reports.FirstOrDefault(r => r.State == UploadQueueState.Pending &&
                        (r.NextAttemptAt is null || r.NextAttemptAt <= runtime.UtcNow));
                    if (report is null) break;
                    report.State = UploadQueueState.Uploading;
                    report.Attempts++;
                    report.NextAttemptAt = null;
                    try { await SaveAsync(report); }
                    catch
                    {
                        report.State = UploadQueueState.Pending;
                        report.NextAttemptAt = runtime.UtcNow.AddSeconds(30);
                        throw;
                    }
                }
                finally { mutations.Release(); }
                RaiseChanged();
                await SendAsync(report, cancellationToken);
            }
        }
        finally
        {
            drain.Release();
            DateTime? due = Reports.Where(r => r.State == UploadQueueState.Pending)
                .Select(r => (DateTime?)(r.NextAttemptAt ?? runtime.UtcNow)).Min();
            timer.Change(due is null ? Timeout.InfiniteTimeSpan :
                TimeSpan.FromMilliseconds(Math.Max(0, (due.Value - runtime.UtcNow).TotalMilliseconds)),
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task SendAsync(QueuedReport report, CancellationToken token)
    {
        await using IAsyncDisposable scope = await background.BeginAsync("upload-report");
        try
        {
            if (report.Receipt is null && report.NetworkAttempted)
            {
                report.Receipt = await uploads.GetReceiptAsync(report.Id, token);
                if (report.Receipt is not null) await SaveAsync(report);
            }
            if (report.Receipt is null)
            {
                QueuedAttribution effective = report.AnonymousFallback
                    ? new QueuedAttribution(new ReportAttribution()) : report.Attribution;
                AccountSession? credentials = effective.CredentialReference is { } reference
                    ? await vault.ResolveAsync(report.Id, reference) : null;
                List<UploadPhoto> photos = [];
                foreach (ReportPhoto photo in report.Photos)
                {
                    byte[] jpeg = await catalog.GetPhotoDataAsync(photo.Id, token)
                        ?? throw new UploadException("One of the photos could not be read.", System.Net.HttpStatusCode.BadRequest);
                    photos.Add(new UploadPhoto(photo.Id, jpeg));
                }
                UploadPreparation preparation = await uploads.PrepareAsync(photos, token);
                ReportDraft draft = ReportDraftMerge.WithServerValues(report.Draft, preparation.PhotoDateTime,
                    preparation.Location, preparation.CrossStreet, uploads.BoundingBox, runtime.UtcNow.ToLocalTime());
                report.NetworkAttempted = true;
                await SaveAsync(report);
                try
                {
                    report.Receipt = await uploads.FinalizeAsync(preparation, draft, effective, credentials, report.Id, token);
                }
                catch (QueuedCredentialRejectedException)
                {
                    // The server rejects credentials before acceptance. Still reconcile a possible
                    // earlier accepted request before changing the effective attribution.
                    report.Receipt = await uploads.GetReceiptAsync(report.Id, token);
                    if (report.Receipt is null)
                    {
                        report.AnonymousFallback = true;
                        await SaveAsync(report);
                        await CleanupCredentialsAsync(() => vault.ReleaseAsync(report.Id));
                        report.Receipt = await uploads.FinalizeAsync(preparation, draft,
                            new QueuedAttribution(new ReportAttribution()), null, report.Id, token);
                    }
                }
                await SaveAsync(report);
            }

            SubmissionReceipt receipt = report.Receipt;
            RaiseChanged();
            await CleanupCredentialsAsync(() => vault.ReleaseAsync(report.Id));
            // Neither credentials nor connectivity are needed to finish accepted local work.
            await catalog.MarkSubmittedAsync(report.Photos, receipt.SubmissionId, token, receipt.SubmittedAt);
            await mutations.WaitAsync(token);
            try
            {
                await store.RemoveAsync(report.Id);
                lock (gate) reports.Remove(report);
            }
            finally { mutations.Release(); }
            RaiseChanged();
            runtime.Dispatch(() => Completed?.Invoke(this, new QueuedReportCompletedEventArgs(report, receipt.SubmissionId)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            report.Attempts = Math.Max(0, report.Attempts - 1);
            report.State = UploadQueueState.Pending;
            await SaveAsync(report);
            RaiseChanged();
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Report {ReportId} remains queued after {FailureType}.", report.Id, ex.GetType().Name);
            UploadException? uploadError = ex as UploadException;
            UploadFailureKind failure = report.Receipt is not null ? UploadFailureKind.Transient :
                UploadRetryPolicy.Classify(uploadError?.StatusCode);
            UploadRetryDecision decision = UploadRetryPolicy.Decide(report.Attempts, failure, runtime.UtcNow,
                uploadError?.RetryAfter);
            report.State = decision.State;
            report.NextAttemptAt = decision.NextAttemptAt;
            report.ServerDirectedRetry = uploadError?.RetryAfter > TimeSpan.Zero;
            report.LastError = report.Receipt is not null ? "Sent; couldn't save photo status. Retry to finish locally." :
                uploadError?.Message ?? "Couldn't finish sending. Your report is saved; retry shortly.";
            await SaveAsync(report);
            RaiseChanged();
        }
    }

    private Task SaveAsync(QueuedReport report) => store.UpdateAsync(Record(report));
    private async Task CleanupCredentialsAsync(Func<Task> cleanup)
    {
        try { await cleanup(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or
            System.Security.Cryptography.CryptographicException or System.Security.SecurityException)
        {
            // The secure vault retains its own references on failure. Reconciliation retries cleanup
            // after restart; unavailable credentials cannot block already-accepted photo recovery.
            logger.LogWarning("Secure credential cleanup is pending ({FailureType}).", ex.GetType().Name);
        }
    }
    private void RaiseChanged() => runtime.Dispatch(() => Changed?.Invoke(this, EventArgs.Empty));
    private void ConnectivityChanged(object? sender, EventArgs e) => Kick();

    private static UploadQueueRecord Record(QueuedReport report) => new UploadQueueRecord
    {
        Id = report.Id,
        CreatedAt = report.CreatedAt,
        State = (int)report.State,
        Attempts = report.Attempts,
        LastError = report.LastError,
        NextAttemptAt = report.NextAttemptAt,
        ServerDirectedRetry = report.ServerDirectedRetry,
        Payload = QueuedReportSerializer.Serialize(new QueuedReportPayload
        {
            Photos = report.Photos.Select(p => new QueuedPhoto { Id = p.Id, Origin = p.Origin }).ToList(),
            Draft = report.Draft,
            Attribution = report.Attribution,
            Receipt = report.Receipt,
            NetworkAttempted = report.NetworkAttempted,
            AnonymousFallback = report.AnonymousFallback
        })
    };

    private static QueuedReport Read(UploadQueueRecord record)
    {
        QueuedReportPayload payload = QueuedReportSerializer.Deserialize(record.Payload)
            ?? throw new InvalidDataException("A saved upload is unreadable; it has not been discarded.");
        if (payload.Attribution is null || payload.Attribution.Intent is null ||
            payload.Receipt is { } receipt && (receipt.ReportId != record.Id ||
                string.IsNullOrWhiteSpace(receipt.SubmissionId) || receipt.SubmittedAt == default))
            throw new InvalidDataException("A saved upload has an invalid delivery state.");
        return new QueuedReport
        {
            Id = record.Id,
            Photos = payload.Photos.Select(p => new ReportPhoto { Id = p.Id, Origin = p.Origin }).ToArray(),
            Draft = payload.Draft,
            CreatedAt = record.CreatedAt,
            State = (UploadQueueState)record.State,
            Attempts = record.Attempts,
            LastError = record.LastError,
            NextAttemptAt = record.NextAttemptAt,
            ServerDirectedRetry = record.ServerDirectedRetry,
            Attribution = payload.Attribution,
            Receipt = payload.Receipt,
            NetworkAttempted = payload.NetworkAttempted,
            AnonymousFallback = payload.AnonymousFallback
        };
    }

    public void Dispose()
    {
        timer.Dispose();
        runtime.ConnectivityChanged -= ConnectivityChanged;
    }
}
