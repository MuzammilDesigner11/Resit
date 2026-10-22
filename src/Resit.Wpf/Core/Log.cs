namespace Resit.Core;

/// <summary>Small append-only diagnostics log in %APPDATA%\Resit\resit.log.</summary>
public static class Log
{
    private static readonly object Sync = new();
    public static string FilePath { get; } = Path.Combine(AppPaths.DataDir, "resit.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never become a second error.
        }
    }
}
