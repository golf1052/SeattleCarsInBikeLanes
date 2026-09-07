namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

/// <summary>
/// Writes new files and flushes their contents to disk using standard .NET APIs.
/// </summary>
/// <remarks>
/// Flushing file contents does not necessarily persist the parent directory's creation, rename,
/// or deletion. The standard .NET APIs used here do not explicitly flush directory metadata, so
/// recent directory changes may be lost after an abrupt OS crash or power loss even after file
/// contents were flushed. Atomic replacement and durable persistence are different guarantees.
/// This residual edge case is deliberately accepted in exchange for using standard .NET APIs;
/// this helper does not provide complete power-loss durability.
/// </remarks>
public static class DurableFile
{
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken token = default)
    {
        await using FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, token);
        stream.Flush(flushToDisk: true);
    }
}
