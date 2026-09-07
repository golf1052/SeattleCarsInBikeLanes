using System.Text.Json;

namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

public interface IWebAuthActionStore
{
    IReadOnlyList<WebAuthAction> Read();
    void Write(IReadOnlyList<WebAuthAction> actions);
}

/// <summary>
/// Saves pending browser actions by flushing a temporary file before replacing the stored record.
/// </summary>
/// <remarks>
/// Directory metadata is not explicitly flushed, so an abrupt OS crash or power loss can lose
/// the latest replacement. Atomic replacement is not a guarantee of durable persistence.
/// </remarks>
public sealed class WebAuthActionStore(string path) : IWebAuthActionStore
{
    public IReadOnlyList<WebAuthAction> Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<List<WebAuthAction>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("The pending sign-out record is unreadable.")
        : [];

    public void Write(IReadOnlyList<WebAuthAction> actions)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, actions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
