using Android.Runtime;
using AndroidX.Work;
using Java.Util.Concurrent;
using SeattleCarsInBikeLanes.Mobile.Services;

namespace SeattleCarsInBikeLanes.Mobile.Platforms.Android;

/// <summary>
/// Registers a single network-constrained WorkManager request for the upload queue.
/// </summary>
public sealed class WorkManagerUploadScheduler : IBackgroundUploadScheduler
{
    internal const string UniqueWorkName =
        "golf1052.SeattleCarsInBikeLanes.Mobile.uploadqueue";

    private const string WorkTag = UniqueWorkName + ".worker";
    private const long InitialBackoffSeconds = 30;

    public void Schedule()
    {
        global::Android.Content.Context context =
            global::Android.App.Application.Context;

        using Constraints constraints = new Constraints.Builder()
            .SetRequiredNetworkType(NetworkType.Connected!)
            .Build();
        using Java.Lang.Class workerClass =
            Java.Lang.Class.FromType(typeof(UploadQueueWorker));
        using OneTimeWorkRequest.Builder builder =
            new OneTimeWorkRequest.Builder(workerClass);

        builder.SetConstraints(constraints);
        builder.SetBackoffCriteria(
            BackoffPolicy.Exponential!,
            InitialBackoffSeconds,
            TimeUnit.Seconds!);
        builder.AddTag(WorkTag);

        using Java.Lang.Object builtRequest = builder.Build();
        using OneTimeWorkRequest request =
            builtRequest.JavaCast<OneTimeWorkRequest>();

        // KEEP is important here. Queue changes also happen while an upload is in flight; replacing
        // that worker would cancel a valid submission and could start it over after the server had
        // already accepted it.
        WorkManager.GetInstance(context).EnqueueUniqueWork(
            UniqueWorkName,
            ExistingWorkPolicy.Keep!,
            request);
    }

    public void Cancel()
    {
        global::Android.Content.Context context =
            global::Android.App.Application.Context;
        WorkManager.GetInstance(context).CancelUniqueWork(UniqueWorkName);
    }
}
