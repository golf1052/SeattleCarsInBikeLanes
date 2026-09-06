using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SeattleCarsInBikeLanes.Mobile.Core.Photos;

public static class DurableFile
{
    public static void CreateDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) CreateDirectory(parent);
        Directory.CreateDirectory(path);
        if (!string.IsNullOrEmpty(parent)) SyncDirectory(parent);
    }

    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken token = default)
    {
        await using FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, token);
        stream.Flush(flushToDisk: true);
    }

    public static void SyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows uses file WriteThrough/FlushFileBuffers; directory descriptors are Unix-only.
            return;
        }
        int descriptor = Open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException("Could not open the photo directory for durable synchronization.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException("Could not synchronize the photo directory.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            Close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
