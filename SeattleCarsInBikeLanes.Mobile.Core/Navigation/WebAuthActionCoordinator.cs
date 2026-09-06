namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

public enum WebAuthProvider
{
    Bluesky,
    Mastodon
}

public enum WebAuthActionKind
{
    OpenSignIn,
    ApplySignedOut
}

public readonly record struct WebAuthAction(
    long Id,
    WebAuthActionKind Kind,
    WebAuthProvider Provider);

/// <summary>
/// Keeps web authentication work pending until the embedded map can apply it.
/// </summary>
public sealed class WebAuthActionCoordinator
{
    private readonly Lock sync = new Lock();
    private readonly List<WebAuthAction> pending = [];
    private long nextId;
    private readonly IWebAuthActionStore? store;

    public WebAuthActionCoordinator(IWebAuthActionStore? store = null)
    {
        this.store = store;
        if (store is not null)
        {
            pending.AddRange(store.Read().Where(action => action.Kind == WebAuthActionKind.ApplySignedOut));
            nextId = pending.Select(action => action.Id).DefaultIfEmpty().Max();
        }
    }

    public event EventHandler? PendingActionsChanged;

    public WebAuthAction QueueOpenSignIn(WebAuthProvider provider)
    {
        WebAuthAction action;
        lock (sync)
        {
            int existingIndex = pending.FindIndex(item =>
                item.Kind == WebAuthActionKind.OpenSignIn);

            if (existingIndex >= 0 && pending[existingIndex].Provider == provider)
            {
                action = pending[existingIndex];
            }
            else
            {
                pending.RemoveAll(item => item.Kind == WebAuthActionKind.OpenSignIn);
                action = CreateAction(WebAuthActionKind.OpenSignIn, provider);
                pending.Add(action);
            }
        }

        RaisePendingActionsChanged();
        return action;
    }

    public WebAuthAction QueueApplySignedOut(WebAuthProvider provider)
    {
        WebAuthAction action;
        lock (sync)
        {
            int existingIndex = pending.FindIndex(item =>
                item.Kind == WebAuthActionKind.ApplySignedOut &&
                item.Provider == provider);

            if (existingIndex >= 0)
            {
                action = CreateAction(WebAuthActionKind.ApplySignedOut, provider);
                Persist(pending.Where((_, index) => index != existingIndex).Append(action));
                pending[existingIndex] = action;
            }
            else
            {
                action = CreateAction(WebAuthActionKind.ApplySignedOut, provider);
                Persist(pending.Append(action));
                pending.Add(action);
            }
        }

        RaisePendingActionsChanged();
        return action;
    }

    public IReadOnlyList<WebAuthAction> GetPendingActions()
    {
        lock (sync)
        {
            return pending.ToArray();
        }
    }

    public bool Acknowledge(long actionId)
    {
        lock (sync)
        {
            Persist(pending.Where(action => action.Id != actionId));
            return pending.RemoveAll(action => action.Id == actionId) != 0;
        }
    }

    public bool HasPending(WebAuthActionKind kind, WebAuthProvider provider)
    {
        lock (sync)
        {
            return pending.Any(action => action.Kind == kind && action.Provider == provider);
        }
    }

    private WebAuthAction CreateAction(WebAuthActionKind kind, WebAuthProvider provider) =>
        new WebAuthAction(Interlocked.Increment(ref nextId), kind, provider);

    private void Persist(IEnumerable<WebAuthAction> actions) =>
        store?.Write(actions.Where(action => action.Kind == WebAuthActionKind.ApplySignedOut).ToArray());

    private void RaisePendingActionsChanged() =>
        PendingActionsChanged?.Invoke(this, EventArgs.Empty);
}
