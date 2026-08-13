using System.Net;

namespace SeattleCarsInBikeLanes.Mobile.Core.Upload;

/// <summary>
/// What should happen to a queued report after an attempt to send it failed.
/// </summary>
/// <param name="State">Where the report goes next.</param>
/// <param name="NextAttemptAt">When it may be tried again, if it may be tried again at all.</param>
public readonly record struct UploadRetryDecision(UploadQueueState State, DateTime? NextAttemptAt);

/// <summary>
/// Decides whether a failed upload is tried again, and when.
/// </summary>
/// <remarks>
/// The server answers a rejected report in plain language and a status code, and the two kinds of
/// answer call for opposite behaviour. "Photo not taken in Seattle" will say the same thing forever,
/// so retrying it burns the user's battery and data to reach a conclusion that was already reached;
/// it has to be put in front of them instead. A dropped connection says nothing about the report at
/// all, and putting that in front of the user makes them do by hand what the app is perfectly able
/// to do for them.
/// </remarks>
public static class UploadRetryPolicy
{
    /// <summary>
    /// How many times a report is sent before it is left for the user.
    /// </summary>
    /// <remarks>
    /// With the backoff below this spans a little over half an hour, which covers a ride through a
    /// tunnel or a patch of no signal without leaving a report retrying for days.
    /// </remarks>
    public const int MaxAttempts = 5;

    /// <summary>
    /// The wait after the first failure, doubled for each one after it.
    /// </summary>
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Works out whether a status code is worth trying again.
    /// </summary>
    /// <param name="statusCode">
    /// The status the server answered with, or null when there was no answer at all.
    /// </param>
    public static UploadFailureKind Classify(HttpStatusCode? statusCode)
    {
        // No status means the request never got an answer: no signal, DNS, a timeout. Nothing about
        // the report itself is known to be wrong.
        if (statusCode is null)
        {
            return UploadFailureKind.Transient;
        }

        int code = (int)statusCode.Value;

        return code switch
        {
            // The server is unwell rather than unhappy with the report.
            >= 500 => UploadFailureKind.Transient,

            (int)HttpStatusCode.RequestTimeout => UploadFailureKind.Transient,
            (int)HttpStatusCode.TooManyRequests => UploadFailureKind.Transient,

            // Everything else in the 4xx range is the server explaining what is wrong with the
            // report, including 403 for a blocked device. Sending it again changes nothing.
            >= 400 => UploadFailureKind.Permanent,

            _ => UploadFailureKind.Transient
        };
    }

    /// <summary>
    /// How long to wait before the next attempt.
    /// </summary>
    /// <param name="attempts">How many attempts have been made, including the one that just failed.</param>
    public static TimeSpan GetBackoff(int attempts)
    {
        if (attempts < 1)
        {
            attempts = 1;
        }

        // Capped before the shift so a long lived queue item cannot overflow its way to a negative
        // wait, which would busy loop the processor.
        int doublings = Math.Min(attempts - 1, 10);
        double seconds = BaseBackoff.TotalSeconds * Math.Pow(2, doublings);

        return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Decides what happens to a report whose latest attempt failed.
    /// </summary>
    /// <param name="attempts">How many attempts have been made, including the one that just failed.</param>
    /// <param name="statusCode">The status the server answered with, if it answered.</param>
    /// <param name="now">The current time.</param>
    public static UploadRetryDecision Decide(int attempts, HttpStatusCode? statusCode, DateTime now) =>
        Decide(attempts, Classify(statusCode), now);

    /// <summary>
    /// Decides what happens to a report whose latest attempt failed for a reason already classified.
    /// </summary>
    /// <param name="attempts">How many attempts have been made, including the one that just failed.</param>
    /// <param name="failure">Whether the failure is worth trying again.</param>
    /// <param name="now">The current time.</param>
    public static UploadRetryDecision Decide(int attempts, UploadFailureKind failure, DateTime now)
    {
        if (failure == UploadFailureKind.Permanent || attempts >= MaxAttempts)
        {
            return new UploadRetryDecision(UploadQueueState.Failed, null);
        }

        return new UploadRetryDecision(UploadQueueState.Pending, now + GetBackoff(attempts));
    }
}
