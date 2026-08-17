using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeattleCarsInBikeLanes.Mobile.Core.Upload;

/// <summary>
/// A photo a queued report is about.
/// </summary>
/// <remarks>
/// Only the identifier is kept. The photo itself stays in the library and is read again when the
/// report is actually sent, so a queue holding four reports does not also hold forty megabytes of
/// JPEG, and so a photo the user edits between queueing and sending goes up as they left it.
/// </remarks>
public sealed class QueuedPhoto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Whether the photo came from the user's library rather than the app's camera.
    /// </summary>
    /// <remarks>
    /// Kept because imported assets and app-owned photos are marked as submitted differently, and
    /// by the time a queued report succeeds the roll it came from is long gone.
    /// </remarks>
    [JsonPropertyName("imported")]
    public bool Imported { get; set; }

    /// <summary>
    /// Whether the JPEG lives in the app's persistent private store rather than the system library.
    /// </summary>
    /// <remarks>
    /// Added as a separate boolean so rows written by older versions, which only carried
    /// <see cref="Imported"/>, continue to deserialize with the same meaning.
    /// </remarks>
    [JsonPropertyName("private")]
    public bool Private { get; set; }
}

/// <summary>
/// Everything needed to send a report, once the user has walked away from it.
/// </summary>
/// <remarks>
/// This is deliberately not the whole report. The Mastodon access token that attribution needs
/// travels inside the finalize body, and writing a credential into a queue file that outlives the
/// process is not a trade worth making for a report that might sit there for an hour. Only the
/// user's wish to be credited is kept, and the credentials are read from secure storage when the
/// report is sent.
/// </remarks>
public sealed class QueuedReportPayload
{
    [JsonPropertyName("photos")]
    public List<QueuedPhoto> Photos { get; set; } = new List<QueuedPhoto>();

    [JsonPropertyName("draft")]
    public ReportDraft Draft { get; set; } = new ReportDraft();
}

/// <summary>
/// Reads and writes the queued report payload.
/// </summary>
public static class QueuedReportSerializer
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public static string Serialize(QueuedReportPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Reads a payload back.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing, because the caller is a queue drained on a background
    /// thread and a row written by an older build must not be able to stop every other report from
    /// going out.
    /// </remarks>
    public static QueuedReportPayload? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            QueuedReportPayload? payload = JsonSerializer.Deserialize<QueuedReportPayload>(json, Options);
            return payload is null || payload.Photos.Count == 0 ? null : payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
