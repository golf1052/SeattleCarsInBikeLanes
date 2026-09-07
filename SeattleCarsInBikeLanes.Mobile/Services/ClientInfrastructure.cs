using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Services;

public sealed class SecureValueStore : ISecureValueStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);
    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);
    public void Remove(string key) => SecureStorage.Default.Remove(key);
}

public sealed class ClientDispatcher : IClientDispatcher
{
    public void Dispatch(Action action) => MainThread.BeginInvokeOnMainThread(action);
}

public sealed class QueueRuntime : IQueueRuntime
{
    public QueueRuntime() => Connectivity.Current.ConnectivityChanged += (_, e) =>
    {
        if (e.NetworkAccess != NetworkAccess.None) ConnectivityChanged?.Invoke(this, EventArgs.Empty);
    };
    public DateTime UtcNow => DateTime.UtcNow;
    public event EventHandler? ConnectivityChanged;
    public void Dispatch(Action action) => MainThread.BeginInvokeOnMainThread(action);
    public void Run(Func<Task> action) => _ = Task.Run(action);
}

/// <summary>Never shares cookies, default bearer headers, or redirect credentials with active login.</summary>
public sealed class NativeReportClient : HttpClient
{
    public NativeReportClient() : base(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        BaseAddress = SiteUrls.BaseAddress;
        Timeout = TimeSpan.FromMinutes(3);
    }
}
