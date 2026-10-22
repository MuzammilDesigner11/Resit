using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Resit.Core;
using Resit.Views;

namespace Resit;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write($"unobserved task error: {args.Exception}");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write($"fatal error: {args.ExceptionObject}");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the instance list leaves running players open; the app ends with the last window.
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = new MainWindow();
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(ShowInstanceList));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static IReadOnlyList<Window> Windows =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? [];

    /// <summary>Brings the instance list forward, reopening it if it was closed while a player kept running.</summary>
    private static void ShowInstanceList()
    {
        var main = Windows.OfType<MainWindow>().FirstOrDefault();
        if (main is null)
        {
            main = new MainWindow();
            main.Show();
        }
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }

    /// <summary>
    /// Last-resort handler for errors on the UI thread. Everything is logged in full. A message
    /// appears only when the user is actively using a Resit window; it never pops up over a game.
    /// </summary>
    private static void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var ex = e.Exception;
        Log.Write($"unhandled error: {ex}");

        var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        var player = Windows.OfType<PlayerWindow>().FirstOrDefault(w => w.IsActive || w.IsVisible);
        if (player is not null)
        {
            player.ShowNotice($"Something went wrong ({message}). Details are in the log.");
            return;
        }
        var active = Windows.FirstOrDefault(w => w.IsActive);
        if (active is not null)
            _ = Dialogs.ShowAsync(active, "Resit", $"{message}\n\nDetails were saved to {Log.FilePath}");
    }
}
