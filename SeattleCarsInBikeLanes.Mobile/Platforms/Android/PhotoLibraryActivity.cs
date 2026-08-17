using Android.App;
using Android.Content;
using Android.OS;

namespace SeattleCarsInBikeLanes.Platforms.Android;

/// <summary>
/// Hosts Android photo-library UI that must receive an activity result.
/// </summary>
[Activity(Exported = false, Theme = "@android:style/Theme.Translucent.NoTitleBar")]
internal sealed class PhotoLibraryActivity : Activity
{
    private const int PickRequestCode = 1;
    private const string OperationExtra = "operation";
    private const string PickOperation = "pick";
    private const string SelectionLimitExtra = "selectionLimit";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (savedInstanceState is not null)
        {
            // Android restores the outstanding activity-for-result request after a configuration
            // change. Relaunching it would stack a second picker over the restored one.
            return;
        }

        try
        {
            switch (Intent?.GetStringExtra(OperationExtra))
            {
                case PickOperation:
                    StartPicker(Intent.GetIntExtra(SelectionLimitExtra, 1));
                    break;
                default:
                    PhotoLibraryActivityCoordinator.Cancel();
                    Finish();
                    break;
            }
        }
        catch (Exception)
        {
            PhotoLibraryActivityCoordinator.Cancel();
            Finish();
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == PickRequestCode)
        {
            IReadOnlyList<global::Android.Net.Uri> uris = resultCode == Result.Ok && data is not null
                ? ReadPickedUris(data)
                : Array.Empty<global::Android.Net.Uri>();

            PhotoLibraryActivityCoordinator.CompletePick(uris);
        }

        Finish();
    }

    private void StartPicker(int selectionLimit)
    {
        Intent picker = new Intent(Intent.ActionOpenDocument)
            .AddCategory(Intent.CategoryOpenable)
            .SetType("image/*")
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);

        picker.PutExtra(Intent.ExtraAllowMultiple, selectionLimit != 1);
        StartActivityForResult(picker, PickRequestCode);
    }

    private IReadOnlyList<global::Android.Net.Uri> ReadPickedUris(Intent data)
    {
        int limit = Intent?.GetIntExtra(SelectionLimitExtra, 1) ?? 1;
        List<global::Android.Net.Uri> uris = new List<global::Android.Net.Uri>();

        if (data.ClipData is global::Android.Content.ClipData clipData)
        {
            for (int index = 0; index < clipData.ItemCount; index++)
            {
                AddUri(clipData.GetItemAt(index)?.Uri);
            }
        }
        else
        {
            AddUri(data.Data);
        }

        return uris;

        void AddUri(global::Android.Net.Uri? uri)
        {
            if (uri is null || (limit > 0 && uris.Count >= limit))
            {
                return;
            }

            try
            {
                ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
                uris.Add(uri);
            }
            catch (Exception)
            {
                // A provider that offered only a temporary grant cannot back a stable imported ID.
            }
        }
    }

    internal static Intent CreatePickIntent(Context context, int selectionLimit) =>
        new Intent(context, typeof(PhotoLibraryActivity))
            .PutExtra(OperationExtra, PickOperation)
            .PutExtra(SelectionLimitExtra, selectionLimit);
}

internal static class PhotoLibraryActivityCoordinator
{
    private static readonly object Sync = new object();
    private static TaskCompletionSource<IReadOnlyList<global::Android.Net.Uri>>? pickCompletion;

    internal static Task<IReadOnlyList<global::Android.Net.Uri>> BeginPick()
    {
        lock (Sync)
        {
            if (pickCompletion is not null)
            {
                return Task.FromResult<IReadOnlyList<global::Android.Net.Uri>>(
                    Array.Empty<global::Android.Net.Uri>());
            }

            pickCompletion = new TaskCompletionSource<IReadOnlyList<global::Android.Net.Uri>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return pickCompletion.Task;
        }
    }

    internal static void CompletePick(IReadOnlyList<global::Android.Net.Uri> uris)
    {
        TaskCompletionSource<IReadOnlyList<global::Android.Net.Uri>>? completion;
        lock (Sync)
        {
            completion = pickCompletion;
            pickCompletion = null;
        }

        completion?.TrySetResult(uris);
    }

    internal static void Cancel() =>
        CompletePick(Array.Empty<global::Android.Net.Uri>());
}
