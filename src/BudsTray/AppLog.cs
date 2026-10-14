using System.IO;

namespace RedmiBudsTray;

internal static class AppLog
{
    private static readonly object Gate = new();
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedmiBudsTray");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512_000)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never interfere with Bluetooth or startup. */ }
    }
}
