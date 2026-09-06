using System.Text.Json;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.Core.Navigation;

public interface IWebAuthActionStore
{
    IReadOnlyList<WebAuthAction> Read();
    void Write(IReadOnlyList<WebAuthAction> actions);
}

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
        DurableFile.SyncDirectory(directory);
    }
}
