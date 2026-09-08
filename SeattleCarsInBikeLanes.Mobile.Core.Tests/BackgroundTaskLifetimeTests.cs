using System.Collections.Concurrent;
using SeattleCarsInBikeLanes.Platforms.iOS;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public sealed class BackgroundTaskLifetimeTests
{
    [Fact]
    public void InitializationDoesNotEndTask()
    {
        List<nint> ended = new List<nint>();
        BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(-1, ended.Add);

        lifetime.Initialize(42);

        Assert.Empty(ended);
    }

    [Fact]
    public void RepeatedEndRequestsEndIdentifierOnlyOnce()
    {
        List<nint> ended = new List<nint>();
        BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(-1, ended.Add);
        lifetime.Initialize(42);

        // Completion and expiration both use End, regardless of which arrives first.
        lifetime.End();
        Assert.Equal((nint)42, Assert.Single(ended));
        lifetime.End();
        lifetime.End();

        Assert.Equal((nint)42, Assert.Single(ended));
    }

    [Fact]
    public void EndBeforeInitializationReleasesEventualIdentifierOnlyOnce()
    {
        List<nint> ended = new List<nint>();
        BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(-1, ended.Add);

        lifetime.End();
        lifetime.End();
        Assert.Empty(ended);

        lifetime.Initialize(42);
        Assert.Equal((nint)42, Assert.Single(ended));
        lifetime.End();

        Assert.Equal((nint)42, Assert.Single(ended));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    public void InvalidIdentifierIsNeverEnded(int invalidIdentifier, bool endBeforeInitialization)
    {
        List<nint> ended = new List<nint>();
        BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(invalidIdentifier, ended.Add);

        if (endBeforeInitialization)
        {
            lifetime.End();
        }

        lifetime.Initialize(invalidIdentifier);
        lifetime.End();
        lifetime.End();

        Assert.Empty(ended);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroIdentifierIsValidWhenSentinelIsNonzero(bool endBeforeInitialization)
    {
        List<nint> ended = new List<nint>();
        BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(-1, ended.Add);

        if (endBeforeInitialization)
        {
            lifetime.End();
        }

        lifetime.Initialize(0);
        lifetime.End();

        Assert.Equal((nint)0, Assert.Single(ended));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantEndDoesNotReleaseIdentifierAgain(bool endBeforeInitialization)
    {
        List<nint> ended = new List<nint>();
        BackgroundTaskLifetime? lifetime = null;
        lifetime = new BackgroundTaskLifetime(-1, identifier =>
        {
            ended.Add(identifier);
            Assert.NotNull(lifetime);
            lifetime.End();
        });

        if (endBeforeInitialization)
        {
            lifetime.End();
        }

        lifetime.Initialize(42);
        lifetime.End();

        Assert.Equal((nint)42, Assert.Single(ended));
    }

    [Fact]
    public async Task ConcurrentEndRequestsReleaseIdentifierOnlyOnce()
    {
        ConcurrentQueue<nint> ended = new ConcurrentQueue<nint>();
        BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(-1, ended.Enqueue);
        lifetime.Initialize(42);
        TaskCompletionSource start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] requests = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            lifetime.End();
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(requests);

        Assert.Equal((nint)42, Assert.Single(ended));
    }

    [Fact]
    public async Task InitializationRacingEndRequestsReleasesIdentifierOnlyOnce()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            ConcurrentQueue<nint> ended = new ConcurrentQueue<nint>();
            BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(-1, ended.Enqueue);
            TaskCompletionSource start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task initialize = Task.Run(async () =>
            {
                await start.Task;
                lifetime.Initialize(42);
            });
            Task[] requests = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                lifetime.End();
            })).ToArray();

            start.SetResult();
            await Task.WhenAll(requests.Append(initialize));
            lifetime.End();

            Assert.Equal((nint)42, Assert.Single(ended));
        }
    }

    [Fact]
    public void ReusedIdentifierBelongsToIndependentLifetimes()
    {
        List<nint> ended = new List<nint>();
        BackgroundTaskLifetime first = new BackgroundTaskLifetime(-1, ended.Add);
        first.Initialize(42);
        first.End();
        BackgroundTaskLifetime second = new BackgroundTaskLifetime(-1, ended.Add);
        second.Initialize(42);

        first.End();
        Assert.Equal((nint)42, Assert.Single(ended));
        second.End();
        second.End();

        Assert.Equal(new nint[] { 42, 42 }, ended);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndFailurePropagatesWithoutRetryingIdentifier(bool endBeforeInitialization)
    {
        List<nint> ended = new List<nint>();
        InvalidOperationException failure = new InvalidOperationException("Native end failed.");
        BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(-1, identifier =>
        {
            ended.Add(identifier);
            throw failure;
        });

        if (endBeforeInitialization)
        {
            lifetime.End();
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => lifetime.Initialize(42)));
        }
        else
        {
            lifetime.Initialize(42);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(lifetime.End));
        }

        lifetime.End();

        Assert.Equal((nint)42, Assert.Single(ended));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReinitializationThrowsWithoutReplacingIdentifier(bool endBeforeReinitialization)
    {
        List<nint> ended = new List<nint>();
        BackgroundTaskLifetime lifetime = new BackgroundTaskLifetime(-1, ended.Add);
        lifetime.Initialize(42);

        if (endBeforeReinitialization)
        {
            lifetime.End();
        }

        Assert.Throws<InvalidOperationException>(() => lifetime.Initialize(43));
        lifetime.End();

        Assert.Equal((nint)42, Assert.Single(ended));
    }
}
