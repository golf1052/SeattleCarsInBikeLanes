using SeattleCarsInBikeLanes.Mobile.Core.Photos;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// A report the user has submitted, on its way to the site.
/// </summary>
public sealed class QueuedReport
{
    public required string Id { get; init; }

    public required IReadOnlyList<ReportPhoto> Photos { get; init; }

    public required ReportDraft Draft { get; init; }

    public DateTime CreatedAt { get; init; }

    public UploadQueueState State { get; set; }

    public int Attempts { get; set; }

    /// <summary>
    /// Why the last attempt failed, in the server's own words where it gave any.
    /// </summary>
    public string? LastError { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    public bool ServerDirectedRetry { get; set; }

    /// <summary>
    /// How the report is described in the list of ones that could not be sent.
    /// </summary>
    public string Description => Photos.Count == 1 ? "1 photo" : $"{Photos.Count} photos";
}

/// <summary>
/// Raised when a queued report reaches the site.
/// </summary>
public sealed class QueuedReportCompletedEventArgs : EventArgs
{
    public QueuedReportCompletedEventArgs(QueuedReport report, string? submissionId)
    {
        Report = report;
        SubmissionId = submissionId;
    }

    public QueuedReport Report { get; }

    public string? SubmissionId { get; }
}

/// <summary>
/// Sends reports in the background, whether or not the user is still looking at the app.
/// </summary>
/// <remarks>
/// Reporting a car in a bike lane happens outdoors, on a phone, usually while the user is on their
/// way somewhere. Making them stand still and watch a progress spinner over a cellular connection is
/// the wrong shape for that. The report is written down first and sent afterwards, so submitting is
/// instant and a lost signal is the app's problem rather than the user's.
/// </remarks>
public interface IUploadQueue
{
    /// <summary>
    /// The reports currently in the queue, oldest first.
    /// </summary>
    IReadOnlyList<QueuedReport> Reports { get; }

    /// <summary>
    /// Raised whenever anything in the queue moves.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Raised when a report has been accepted by the site.
    /// </summary>
    event EventHandler<QueuedReportCompletedEventArgs>? Completed;

    /// <summary>
    /// Loads the queue from disk and starts sending.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Adds a report to the queue and starts sending it.
    /// </summary>
    /// <returns>False for an empty selection or photos already submitted or in another queued report.</returns>
    Task<bool> EnqueueAsync(IReadOnlyList<ReportPhoto> photos,
        ReportDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Where a photo has got to, if it is in the queue at all.
    /// </summary>
    UploadQueueState? GetPhotoState(string photoId);

    /// <summary>
    /// Puts a failed report back in the queue at the user's request.
    /// </summary>
    Task RetryAsync(string id);

    /// <summary>
    /// Throws a failed report away at the user's request.
    /// </summary>
    Task DiscardAsync(string id);

    /// <summary>
    /// Nudges the queue to have another go, without waiting for it.
    /// </summary>
    void Kick();

    /// <summary>
    /// Sends everything that is ready to be sent.
    /// </summary>
    /// <remarks>
    /// Awaitable so the iOS background task can hold the app awake until the queue is empty and
    /// then tell the system it has finished.
    /// </remarks>
    Task DrainAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class UploadQueue : IUploadQueue
{
    private readonly IUploadQueueStore store;
    private readonly IUploadService uploadService;
    private readonly IPhotoCatalog photoCatalog;
    private readonly IAuthService authService;
    private readonly IBackgroundWorkScope backgroundWork;
    private readonly ILogger<UploadQueue> logger;

    /// <summary>
    /// Guards <see cref="reports"/>, which is read from the UI thread and written from the drain.
    /// </summary>
    private readonly object gate = new object();

    private readonly List<QueuedReport> reports = new List<QueuedReport>();

    /// <summary>
    /// Held for the whole of a drain, so only one report is ever in flight.
    /// </summary>
    /// <remarks>
    /// Sending them one at a time is deliberate. Several reports at once on a phone connection makes
    /// all of them slower and none of them more likely to finish, and the server hands out one
    /// submission id per initial upload anyway.
    /// </remarks>
    private readonly SemaphoreSlim drainMutex = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Fires when a report that backed off is due to be tried again.
    /// </summary>
    private readonly System.Threading.Timer retryTimer;

    private bool started;

    public UploadQueue(    IUploadQueueStore store,
    IUploadService uploadService,
    IPhotoCatalog photoCatalog,
    IAuthService authService,
        IBackgroundWorkScope backgroundWork,
        ILogger<UploadQueue> logger)
    {
        this.store = store;
        this.uploadService = uploadService;
        this.photoCatalog = photoCatalog;
        this.authService = authService;
        this.backgroundWork = backgroundWork;
        this.logger = logger;

        retryTimer = new System.Threading.Timer(_ => Kick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<QueuedReport> Reports
    {
        get
        {
            lock (gate)
            {
                return reports.ToList();
            }
        }
    }

    public event EventHandler? Changed;

    public event EventHandler<QueuedReportCompletedEventArgs>? Completed;

    public async Task StartAsync()
    {
        if (started)
        {
            return;
        }

        started = true;

        try
        {
            await store.ResetInterruptedAsync();

            IReadOnlyList<UploadQueueRecord> records = await store.GetAllAsync();
            List<string> unreadable = new List<string>();

            lock (gate)
            {
                reports.Clear();

                foreach (UploadQueueRecord record in records)
                {
                    QueuedReport? report = TryRead(record);
                    if (report is null)
                    {
                        unreadable.Add(record.Id);
                        continue;
                    }

                    reports.Add(report);
                }
            }

            foreach (string id in unreadable)
            {
                logger.LogWarning("Dropping unreadable queued report {Id}.", id);
                await store.RemoveAsync(id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load the upload queue.");
        }

        // A report that has been waiting since the last time the app ran should not have to wait for
        // the user to do something before it is tried again.
        Connectivity.Current.ConnectivityChanged += ConnectivityChanged;

        RaiseChanged();
        Kick();
    }

    public async Task<bool> EnqueueAsync(IReadOnlyList<ReportPhoto> photos,
        ReportDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(draft);

        if (photos.Count == 0)
        {
            return false;
        }

        if (photos.Any(photo => photo.Submitted))
        {
            logger.LogWarning("Refusing to queue photos that have already been reported.");
            return false;
        }

        QueuedReport report = new QueuedReport()
        {
            Id = Guid.NewGuid().ToString("N"),
            Photos = photos.ToList(),
            Draft = draft.Clone(),
            CreatedAt = DateTime.UtcNow,
            State = UploadQueueState.Pending
        };

        lock (gate)
        {
            // A photo is spoken for from the moment it is queued. Without this a user who taps
            // submit, comes back to a roll that has not caught up yet, and reports the same photo
            // again would put two copies of it on the site.
            HashSet<string> queued = reports
                .SelectMany(existing => existing.Photos)
                .Select(photo => photo.Id)
                .ToHashSet(StringComparer.Ordinal);

            if (photos.Any(photo => queued.Contains(photo.Id)))
            {
                return false;
            }

            reports.Add(report);
        }

        await store.AddAsync(ToRecord(report));

        RaiseChanged();
        Kick();
        return true;
    }

    public UploadQueueState? GetPhotoState(string photoId)
    {
        if (string.IsNullOrEmpty(photoId))
        {
            return null;
        }

        lock (gate)
        {
            foreach (QueuedReport report in reports)
            {
                foreach (ReportPhoto photo in report.Photos)
                {
                    if (string.Equals(photo.Id, photoId, StringComparison.Ordinal))
                    {
                        return report.State;
                    }
                }
            }
        }

        return null;
    }

    public async Task RetryAsync(string id)
    {
        QueuedReport? report = Find(id);
        if (report is null || report.State != UploadQueueState.Failed)
        {
            return;
        }

        // The attempt count starts over: the user has looked at the failure and asked for it to be
        // tried again, which is a different thing from the app deciding to on its own.
        report.State = UploadQueueState.Pending;
        report.Attempts = 0;
        report.LastError = null;
        report.NextAttemptAt = null;
        report.ServerDirectedRetry = false;

        await SaveAsync(report);
        RaiseChanged();
        Kick();
    }

    public async Task DiscardAsync(string id)
    {
        QueuedReport? report = Find(id);
        if (report is null)
        {
            return;
        }

        lock (gate)
        {
            reports.Remove(report);
        }

        await store.RemoveAsync(report.Id);
        RaiseChanged();
    }

    public void Kick()
    {
        // Deliberately not awaited: this is called from event handlers, from the UI thread, and from
        // the drain's own timer, none of which should be made to wait on an upload.
        _ = Task.Run(async () =>
        {
            try
            {
                await DrainAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // The app is going away mid upload, which the queue is built for.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The upload queue stopped unexpectedly.");
            }
        });
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        if (!await drainMutex.WaitAsync(0, cancellationToken))
        {
            // Already draining. Whatever was just added will be picked up by the loop below.
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                QueuedReport? next = await TakeNextReadyAsync(DateTime.UtcNow);
                if (next is null)
                {
                    break;
                }

                await SendAsync(next, cancellationToken);
            }
        }
        finally
        {
            drainMutex.Release();
            ScheduleNextAttempt();
        }
    }

    /// <summary>
    /// Sends one report, start to finish.
    /// </summary>
    /// <remarks>
    /// Always from the initial upload, never from a half finished one. The server keeps the blobs
    /// from an initial upload for ten minutes and then deletes them, so a finalize call made against
    /// an older attempt would refer to photos that are no longer there.
    /// </remarks>
    private async Task SendAsync(QueuedReport report, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable scope = await backgroundWork.BeginAsync("upload-report");

        try
        {
            List<UploadPhoto> toUpload = new List<UploadPhoto>(report.Photos.Count);
            foreach (ReportPhoto photo in report.Photos)
            {
                byte[]? jpeg = await photoCatalog.GetPhotoDataAsync(photo.Id, cancellationToken);
                if (jpeg is null)
                {
                    // The photo was deleted between queueing and sending. No amount of retrying
                    // brings it back, so the user has to be told.
                    await FailAsync(report,
                        "One of the photos could not be read. It may have been deleted.",
                        UploadFailureKind.Permanent);
                    return;
                }

                toUpload.Add(new UploadPhoto(photo.Id, jpeg));
            }

            UploadPreparation preparation = await uploadService.PrepareAsync(toUpload, cancellationToken);

            ReportDraft draft = ReportDraftMerge.WithServerValues(report.Draft,
                preparation.PhotoDateTime,
                preparation.Location,
                preparation.CrossStreet,
                uploadService.BoundingBox,
                DateTime.Now);

            // Read at send time rather than taken from the queue, because the finalize body carries
            // the user's Mastodon access token and that is not something to write to disk.
            AttributionIdentity? identity = draft.Attribute ? authService.CurrentIdentity : null;

            await uploadService.FinalizeAsync(preparation, draft, identity, report.Id, cancellationToken);

            // Past this line the report is on the site, and nothing that goes wrong afterwards may
            // be allowed to send it again. It leaves the queue first, so a failure to stamp the
            // photos or to tidy the row can only cost a duplicate looking tile in the roll rather
            // than a duplicate report on the site.
            lock (gate)
            {
                reports.Remove(report);
            }

            try
            {
                await store.RemoveAsync(report.Id);
            }
            catch (Exception ex)
            {
                // Left behind, the row would be picked up as an interrupted upload next launch and
                // sent a second time, so this is worth being loud about.
                logger.LogError(ex, "Report {Id} was sent but could not be cleared from the queue.", report.Id);
            }

            try
            {
                await photoCatalog.MarkSubmittedAsync(report.Photos, preparation.SubmissionId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Report {Id} was sent but its photos could not be marked submitted.", report.Id);
            }

            RaiseChanged();
            RaiseCompleted(report, preparation.SubmissionId);
        }
        catch (UploadException ex)
        {
            if (ex.IsReportInFlight)
            {
                // This 429 comes from this report's own server-side claim, which will either
                // complete or become eligible for takeover. Do not exhaust the user's retry budget
                // while waiting for that bounded state transition.
                report.Attempts = Math.Max(report.Attempts - 1, 0);
            }

            await FailAsync(report,
                ex.IsBlocked ? "This device can't submit reports." : ex.Message,
                UploadRetryPolicy.Classify(ex.StatusCode),
                ex.RetryAfter);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is being suspended, not the report being rejected. It goes back in the queue
            // untouched, without counting against the attempt cap, so the next drain starts it
            // again from the beginning.
            report.Attempts = Math.Max(report.Attempts - 1, 0);
            report.State = UploadQueueState.Pending;
            await SaveAsync(report);
            RaiseChanged();
            throw;
        }
        catch (OperationCanceledException)
        {
            // Nobody asked for this to stop, so it is HttpClient giving up on a request that took
            // longer than its timeout. That is a slow connection rather than a bad report, and it
            // is worth another go once the backoff has passed.
            await FailAsync(report,
                "The site took too long to respond. Trying again shortly.",
                UploadFailureKind.Transient);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send queued report {Id}.", report.Id);
            await FailAsync(report,
                "Couldn't reach the site. Check your connection and try again.",
                UploadFailureKind.Transient);
        }
    }

    private async Task FailAsync(QueuedReport report,
        string message,
        UploadFailureKind kind,
        TimeSpan? retryAfter = null)
    {
        report.LastError = message;

        UploadRetryDecision decision =
            UploadRetryPolicy.Decide(report.Attempts, kind, DateTime.UtcNow, retryAfter);

        report.State = decision.State;
        report.NextAttemptAt = decision.NextAttemptAt;
        report.ServerDirectedRetry =
            decision.NextAttemptAt.HasValue && retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero;

        if (decision.State == UploadQueueState.Failed)
        {
            logger.LogWarning("Queued report {Id} was not sent: {Message}", report.Id, message);
        }

        await SaveAsync(report);
        RaiseChanged();
    }

    /// <summary>
    /// Claims the next report that is ready to go, marking it in flight before releasing the lock.
    /// </summary>
    private async Task<QueuedReport?> TakeNextReadyAsync(DateTime now)
    {
        QueuedReport? next;

        lock (gate)
        {
            next = reports.FirstOrDefault(report =>
                report.State == UploadQueueState.Pending &&
                (report.NextAttemptAt is null || report.NextAttemptAt <= now));

            if (next is null)
            {
                return null;
            }

            next.State = UploadQueueState.Uploading;
            next.Attempts++;
            next.NextAttemptAt = null;
            next.ServerDirectedRetry = false;
        }

        // Written down before the request goes out, so a process that dies mid upload leaves a
        // record that startup can recognise and put back.
        await SaveAsync(next);
        RaiseChanged();
        return next;
    }

    /// <summary>
    /// Wakes the queue up when the next report is due.
    /// </summary>
    /// <remarks>
    /// A report that is ready right now matters as much as one waiting out a backoff. Enqueueing
    /// happens on the UI thread and can land in the moment between the drain loop finding nothing
    /// left and the drain actually finishing, in which case its own kick was turned away for a
    /// drain that was already on its way out. Without waking for those the report would sit there
    /// until the user next opened the app.
    /// </remarks>
    private void ScheduleNextAttempt()
    {
        DateTime? earliest = null;

        lock (gate)
        {
            foreach (QueuedReport report in reports.Where(report => report.State == UploadQueueState.Pending))
            {
                DateTime due = report.NextAttemptAt ?? DateTime.UtcNow;
                if (earliest is null || due < earliest)
                {
                    earliest = due;
                }
            }
        }

        if (earliest is null)
        {
            retryTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        TimeSpan delay = earliest.Value - DateTime.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        retryTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void ConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess != NetworkAccess.None)
        {
            // Coming back into signal is the most likely reason a backed off report would now
            // succeed, so it does not have to sit out the rest of its wait.
            ClearBackoff();
            Kick();
        }
    }

    private void ClearBackoff()
    {
        List<QueuedReport> cleared = new List<QueuedReport>();

        lock (gate)
        {
            foreach (QueuedReport report in reports.Where(report =>
                report.State == UploadQueueState.Pending &&
                report.NextAttemptAt.HasValue &&
                !report.ServerDirectedRetry))
            {
                report.NextAttemptAt = null;
                cleared.Add(report);
            }
        }

        foreach (QueuedReport report in cleared)
        {
            _ = SaveAsync(report);
        }
    }
    private QueuedReport? Find(string id)
    {
        lock (gate)
        {
            return reports.FirstOrDefault(report => string.Equals(report.Id, id, StringComparison.Ordinal));
        }
    }

    private async Task SaveAsync(QueuedReport report)
    {
        try
        {
            await store.UpdateAsync(ToRecord(report));
        }
        catch (Exception ex)
        {
            // Losing the record of an attempt is not worth abandoning the report over: the in memory
            // copy is still correct for this run of the app.
            logger.LogWarning(ex, "Could not persist queued report {Id}.", report.Id);
        }
    }

    private static UploadQueueRecord ToRecord(QueuedReport report) => new UploadQueueRecord()
    {
        Id = report.Id,
        Payload = QueuedReportSerializer.Serialize(new QueuedReportPayload()
        {
            Photos = report.Photos.Select(photo => new QueuedPhoto()
            {
                Id = photo.Id,
                Origin = photo.Origin
            }).ToList(),
            Draft = report.Draft
        }),
        CreatedAt = report.CreatedAt,
        State = (int)report.State,
        Attempts = report.Attempts,
        LastError = report.LastError,
        NextAttemptAt = report.NextAttemptAt,
        ServerDirectedRetry = report.ServerDirectedRetry
    };

    private static QueuedReport? TryRead(UploadQueueRecord record)
    {
        QueuedReportPayload? payload = QueuedReportSerializer.Deserialize(record.Payload);
        if (payload is null)
        {
            return null;
        }

        return new QueuedReport()
        {
            Id = record.Id,
            Photos = payload.Photos.Select(photo => new ReportPhoto()
            {
                Id = photo.Id,
                Origin = photo.Origin
            }).ToList(),
            Draft = payload.Draft,
            CreatedAt = record.CreatedAt,
            State = (UploadQueueState)record.State,
            Attempts = record.Attempts,
            LastError = record.LastError,
            NextAttemptAt = record.NextAttemptAt,
            ServerDirectedRetry = record.ServerDirectedRetry
        };
    }

    private void RaiseChanged() =>
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke(this, EventArgs.Empty));

    private void RaiseCompleted(QueuedReport report, string? submissionId) =>
        MainThread.BeginInvokeOnMainThread(() =>
            Completed?.Invoke(this, new QueuedReportCompletedEventArgs(report, submissionId)));
}
