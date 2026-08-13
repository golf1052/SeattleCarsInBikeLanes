using System.Net;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

/// <summary>
/// The rules that decide whether a report the site refused is tried again.
/// </summary>
/// <remarks>
/// Getting these the wrong way round is expensive in both directions. Retrying something the server
/// will always refuse spends a user's battery and cellular data to reach a conclusion that was
/// already reached, and giving up on a dropped connection makes the user do by hand what the queue
/// exists to do for them.
/// </remarks>
public class UploadRetryPolicyTests
{
    [Fact]
    public void NoResponseIsTransient()
    {
        // A request that never got an answer says nothing about the report.
        Assert.Equal(UploadFailureKind.Transient, UploadRetryPolicy.Classify(null));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void ServerTroubleIsTransient(HttpStatusCode statusCode)
    {
        Assert.Equal(UploadFailureKind.Transient, UploadRetryPolicy.Classify(statusCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public void RejectionIsPermanent(HttpStatusCode statusCode)
    {
        // These are the server explaining what is wrong with the report. Sending it again unchanged
        // gets the same answer.
        Assert.Equal(UploadFailureKind.Permanent, UploadRetryPolicy.Classify(statusCode));
    }

    [Fact]
    public void BlockedDeviceIsPermanent()
    {
        Assert.Equal(UploadFailureKind.Permanent, UploadRetryPolicy.Classify(HttpStatusCode.Forbidden));
    }

    [Fact]
    public void PermanentFailureStopsImmediately()
    {
        DateTime now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        UploadRetryDecision decision = UploadRetryPolicy.Decide(1, HttpStatusCode.BadRequest, now);

        Assert.Equal(UploadQueueState.Failed, decision.State);
        Assert.Null(decision.NextAttemptAt);
    }

    [Fact]
    public void TransientFailureIsScheduledForLater()
    {
        DateTime now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        UploadRetryDecision decision = UploadRetryPolicy.Decide(1, HttpStatusCode.ServiceUnavailable, now);

        Assert.Equal(UploadQueueState.Pending, decision.State);
        Assert.NotNull(decision.NextAttemptAt);
        Assert.True(decision.NextAttemptAt > now);
    }

    [Fact]
    public void GivesUpAfterTheAttemptCap()
    {
        DateTime now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Even a failure that would otherwise be worth retrying stops here, so a report that can
        // never be sent does not retry against a phone's battery forever.
        UploadRetryDecision decision = UploadRetryPolicy.Decide(UploadRetryPolicy.MaxAttempts, null, now);

        Assert.Equal(UploadQueueState.Failed, decision.State);
        Assert.Null(decision.NextAttemptAt);
    }

    [Fact]
    public void BackoffGrowsWithEachAttempt()
    {
        Assert.True(UploadRetryPolicy.GetBackoff(2) > UploadRetryPolicy.GetBackoff(1));
        Assert.True(UploadRetryPolicy.GetBackoff(3) > UploadRetryPolicy.GetBackoff(2));
    }

    [Fact]
    public void BackoffIsCappedAndAlwaysPositive()
    {
        // A long lived queue item must not be able to shift its way to a negative or absurd wait,
        // which would either busy loop the processor or strand the report.
        for (int attempts = 1; attempts < 200; attempts++)
        {
            TimeSpan backoff = UploadRetryPolicy.GetBackoff(attempts);

            Assert.True(backoff > TimeSpan.Zero);
            Assert.True(backoff <= TimeSpan.FromMinutes(15));
        }
    }
}
