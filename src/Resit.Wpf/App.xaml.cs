using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Resit.Core;
using Resit.Views;

namespace Resit;

public partial class App : Application
{
    // One Resit per user session: launching it again brings the running copy forward instead
    // of opening a second instance list.
    private const string InstanceMutexName = @"Local\Resit.SingleInstance";
    private const string ActivateEventName = @"Local\Resit.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Let the running copy take the foreground (only the launched process may grant
            // that), ask it to show itself, and quit.
            const int ASFW_ANY = -1;
            AllowSetForegroundWindow(ASFW_ANY);
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var running)) running.Set();
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var listener = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowInstanceList);
        }) { IsBackground = true, Name = "Resit activation" };
        listener.Start();

        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write($"unobserved task error: {args.Exception}");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write($"fatal error: {args.ExceptionObject}");

        base.OnStartup(e);
        new MainWindow().Show();
    }

    /// <summary>Brings the instance list forward, reopening it if it was closed while a player kept running.</summary>
    private void ShowInstanceList()
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

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Last-resort handler for errors on the UI thread (including background work such as the
    /// app watcher). Everything is logged in full. A dialog appears only when the user is
    /// actively using a Resit window; it never pops up over a game or video.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var ex = e.Exception;

        // WPF bug: opening a dropdown, menu or tooltip while an accessibility client is listening
        // can fail inside Popup's MSAA-to-UIA bridge with an empty FileNotFoundException
        // (0x80070002), leaving the popup built but hidden. Show it ourselves.
        if (ex is FileNotFoundException && ex.StackTrace?.Contains("ForceMsaaToUiaBridge") == true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                Log.Write($"WPF popup accessibility error (ForceMsaaToUiaBridge); showed {PopupRecovery.ShowStranded()} popup(s)"));
            return;
        }

        Log.Write($"unhandled error: {ex}");

        var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        var player = Windows.OfType<PlayerWindow>().FirstOrDefault(w => w.IsActive || w.IsVisible);
        if (player is not null)
        {
            player.ShowNotice($"Something went wrong ({message}). Details are in the log.");
            return;
        }
        if (Windows.OfType<Window>().Any(w => w.IsActive))
            MessageBox.Show($"{message}\n\nDetails were saved to {Log.FilePath}", "Resit", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
