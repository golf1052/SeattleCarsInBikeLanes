namespace SeattleCarsInBikeLanes.Mobile.Services;

/// <summary>
/// Shrinks photos before upload.
/// </summary>
public interface IImageResizer
{
    /// <summary>
    /// Returns a JPEG whose longest edge is at most <paramref name="maxEdge"/>.
    /// </summary>
    /// <remarks>
    /// Implementations must preserve EXIF and XMP. The server reads the date and GPS out of the
    /// uploaded file before it does anything else, so a resize that drops metadata would turn every
    /// upload into a form the user has to fill in by hand.
    /// </remarks>
    Task<byte[]> ResizeAsync(byte[] jpeg, int maxEdge, CancellationToken cancellationToken = default);
}

/// <summary>
/// Leaves photos exactly as they are.
/// </summary>
/// <remarks>
/// Used where no platform resizer exists. Uploading the original is slower but always correct,
/// which is the right way round for a fallback.
/// </remarks>
public sealed class PassthroughImageResizer : IImageResizer
{
    public Task<byte[]> ResizeAsync(byte[] jpeg, int maxEdge, CancellationToken cancellationToken = default) =>
        Task.FromResult(jpeg);
}
