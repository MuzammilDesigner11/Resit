using Avalonia;
using Resit.Core;

namespace Resit;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // One Resit per user session: starting it again brings the running copy forward.
        if (!SingleInstance.Claim()) return 0;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Write($"fatal error: {ex}");
            throw;
        }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
