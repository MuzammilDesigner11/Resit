using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Resit.Core;
using Resit.Native;

namespace Resit.Views;

/// <summary>
/// Runs one instance: embeds the emulator window, adds the side toolbar and drives game
/// controls (keyboard to multi-touch) through the overlay.
/// </summary>
public partial class PlayerWindow : Window
{
    private static readonly Dictionary<string, PlayerWindow> _open = [];

    public static event EventHandler? SessionsChanged;

    public static bool IsOpen(string name) => _open.ContainsKey(name);

    public static void Open(SdkLocation sdk, Instance instance, StartOptions options)
    {
        if (_open.TryGetValue(instance.Name, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        instance.Reload();
        var window = new PlayerWindow(sdk, instance, options);
        _open[instance.Name] = window;
        SessionsChanged?.Invoke(null, EventArgs.Empty);
        window.Show();
    }

    private const double ToolbarWidth = 54;

    private readonly SdkLocation _sdk;
    private readonly Instance _instance;
    private readonly StartOptions _startOptions;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _fitTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private EmulatorSession? _session;
    private KeymapOverlay? _overlay;
    private KeymapEngine? _engine;
    private KeyboardHook? _hook;
    private FullscreenBar? _fullscreenBar;
    private ScreenGeometry _geometry;
    private IntPtr _hwnd;
    private bool _ready;
    private bool _closing;
    private bool _closed;
    private bool _polling;
    private string _package = KeymapStore.Default;
    private int? _lastAndroidRotation;
    private bool _typing;
    private bool _pasting;
    private readonly HashSet<int> _keysDown = [];

    private bool _fullscreen;
    private WindowState _savedState;
    private Rect _savedBounds;
    private DateTime _barHideAt;

    private PlayerWindow(SdkLocation sdk, Instance instance, StartOptions options)
    {
        InitializeComponent();
        _sdk = sdk;
        _instance = instance;
        _startOptions = options;
        _geometry = new ScreenGeometry(instance.Width, instance.Height, 0);
        Title = $"{instance.DisplayName} · Resit";
        SizeToScreen();

        SourceInitialized += (_, _) => _hwnd = new WindowInteropHelper(this).Handle;
        Loaded += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Closed += OnClosed;
        Deactivated += (_, _) =>
        {
            _engine?.ReleaseAll();
            ReleaseForwardedKeys();
        };
        LocationChanged += (_, _) => UpdateOverlayBounds();
        StateChanged += (_, _) => UpdateOverlayBounds();
        _fitTimer.Tick += (_, _) => KeepEmbedded();
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _edgeTimer.Tick += (_, _) => UpdateFullscreenBar();
    }

    private EmulatorClient? Client => _session?.Client;

    /// <summary>Opens as large as fits in 85% of the screen, keeping the device's aspect ratio.</summary>
    private void SizeToScreen()
    {
        var work = SystemParameters.WorkArea;
        double chromeW = ToolbarWidth + 16, chromeH = SystemParameters.WindowCaptionHeight + 16;
        double maxW = work.Width * 0.85 - chromeW, maxH = work.Height * 0.85 - chromeH;
        double w = maxW, h = w / _geometry.ShownAspect;
        if (h > maxH) { h = maxH; w = h * _geometry.ShownAspect; }
        Width = w + chromeW;
        Height = h + chromeH;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private void ShowProgress(string stage, double percent, string detail)
    {
        LoadingText.Text = stage;
        LoadingBar.Value = Math.Clamp(percent, 0, 100);
        LoadingPercent.Text = $"{LoadingBar.Value:0}%";
        LoadingDetail.Text = detail;
    }

    private async Task StartAsync()
    {
        try
        {
            ShowProgress("Starting emulator", 3, _instance.ImageSummary);
            var launch = new Progress<string>(s => ShowProgress(s, 5, _instance.ImageSummary));
            _session = await EmulatorSession.StartAsync(_sdk, _instance, _startOptions, launch, _cts.Token);
            _session.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_closed) return;
                _closing = true;
                Close();
            });

            ShowProgress("Starting the virtual device", 8, "");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            IntPtr window;
            // Poll fast: the emulator shows its own window briefly before it is pulled in here.
            while ((window = _session.FindMainWindow()) == IntPtr.Zero)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The emulator window did not appear.");
                await Task.Delay(30, _cts.Token);
            }
            Host.Embed(window);   // still hidden behind the progress panel
            HideEmulatorExtras();
            _fitTimer.Start();

            Title = $"{_instance.DisplayName} · Starting… · Resit";
            var boot = new Progress<BootProgress>(p =>
                ShowProgress(p.Stage, p.Percent, $"{(int)p.Elapsed.TotalSeconds} s"));
            await _session.WaitForBootAsync(boot, _cts.Token);

            if (_instance.Options.Edition == Edition.Minimal && !_instance.Options.MinimalSetupDone)
            {
                var setup = new Progress<BootProgress>(p => ShowProgress(p.Stage, p.Percent, ""));
                // Marked done only when every extra app is gone; otherwise it retries next start.
                if (await MinimalEdition.ApplyAsync(_session.Adb, setup, _cts.Token))
                {
                    _instance.Options.MinimalSetupDone = true;
                    _instance.Options.Save(_instance.Dir);
                }
            }

            ShowProgress("Almost ready", 100, "");
            try { await _session.PrepareForPlayAsync(_instance.Options.BlockAds); }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { }

            await RefreshRotationAsync();
            LayoutDisplay();
            Host.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Collapsed;
            NudgeLater(0.3, 1.5, 4);
            Title = $"{_instance.DisplayName} · Resit";

            _engine = new KeymapEngine(Client!, () => _geometry) { Profile = KeymapStore.Load(KeymapStore.Default) };
            _overlay = new KeymapOverlay(this) { ShowHints = _instance.Options.ShowKeyHints };
            _overlay.SetProfile(_engine.Profile, KeymapStore.Default);
            _overlay.Saved += profile => _engine.Profile = profile;
            _overlay.EditingEnded += () =>
            {
                ControlsToggle.IsChecked = false;
                Activate();
                Host.FocusEmulator();
            };
            _overlay.Show();
            UpdateOverlayBounds();
            _hook = new KeyboardHook(IsGameInputActive, OnHookKey);
            _ready = true;
            if (_instance.Options.StartFullscreen) ToggleFullscreen();
            Activate();
            Host.FocusEmulator();

            _pollTimer.Start();
            await PollAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write($"start failed: {ex}");
            Host.Visibility = Visibility.Hidden;
            LoadingPanel.Visibility = Visibility.Visible;
            ShowProgress("Could not start the instance", 0, ex.Message);
            LoadingPercent.Text = "";
        }
    }

    // ---- Keeping the embedded window in shape ---------------------------------------

    private void KeepEmbedded()
    {
        Host.KeepFitted();
        HideEmulatorExtras();
        UpdateOverlayBounds();
    }

    /// <summary>The emulator ignores resizes until its first frame exists, so repeat a few times.</summary>
    private async void NudgeLater(params double[] seconds)
    {
        double elapsed = 0;
        foreach (var s in seconds)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(s - elapsed), _cts.Token); }
            catch (OperationCanceledException) { return; }
            elapsed = s;
            Host.Nudge();
        }
    }

    /// <summary>Hides the emulator's own floating toolbar; Resit's sidebar replaces it.</summary>
    private void HideEmulatorExtras()
    {
        if (_session is null) return;
        foreach (var w in _session.FindToolbarWindows())
            if (Win32.IsWindowVisible(w)) Win32.ShowWindow(w, Win32.SW_HIDE);
    }

    private void OnDisplayAreaSizeChanged(object sender, SizeChangedEventArgs e) => LayoutDisplay();

    /// <summary>Sizes the screen to the largest rectangle with the device's aspect ratio.</summary>
    private void LayoutDisplay()
    {
        double w = DisplayArea.ActualWidth, h = DisplayArea.ActualHeight;
        if (w < 1 || h < 1) return;
        double aspect = _geometry.ShownAspect;
        if (w / h > aspect) w = h * aspect; else h = w / aspect;
        Host.Width = Math.Floor(w);
        Host.Height = Math.Floor(h);
        Dispatcher.BeginInvoke(UpdateOverlayBounds, DispatcherPriority.Render);
    }

    /// <summary>Lays the overlay exactly over the emulator's screen (which the emulator centres itself).</summary>
    private void UpdateOverlayBounds()
    {
        if (_overlay is null || PresentationSource.FromVisual(Host) is not { CompositionTarget: { } target }) return;
        if (WindowState == WindowState.Minimized || Host.EmulatorWindow == IntPtr.Zero) return;
        Win32.GetWindowRect(Host.EmulatorWindow, out var r);
        if (r.Width <= 0 || r.Height <= 0) return;
        var topLeft = target.TransformFromDevice.Transform(new Point(r.Left, r.Top));
        var bottomRight = target.TransformFromDevice.Transform(new Point(r.Right, r.Bottom));
        if (Math.Abs(_overlay.Left - topLeft.X) > 0.5) _overlay.Left = topLeft.X;
        if (Math.Abs(_overlay.Top - topLeft.Y) > 0.5) _overlay.Top = topLeft.Y;
        if (Math.Abs(_overlay.Width - (bottomRight.X - topLeft.X)) > 0.5) _overlay.Width = bottomRight.X - topLeft.X;
        if (Math.Abs(_overlay.Height - (bottomRight.Y - topLeft.Y)) > 0.5) _overlay.Height = bottomRight.Y - topLeft.Y;
    }

    // ---- Game controls ---------------------------------------------------------------

    private bool IsGameInputActive() =>
        _engine is not null && _overlay is { IsEditing: false } && _hwnd != IntPtr.Zero &&
        Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT) == _hwnd;

    /// <summary>
    /// Every key pressed while the player is in front. Game controls get first pick; Windows
    /// shortcuts stay with Windows; everything else is typed into Android over gRPC. (The
    /// embedded emulator window cannot take keyboard input itself: Qt only accepts keys while
    /// its own top-level window is active, and inside Resit it never is.)
    /// </summary>
    private bool OnHookKey(Key key, int scanCode, bool isDown)
    {
        if (key == Key.F11)
        {
            if (isDown) Dispatcher.BeginInvoke(ToggleFullscreen);
            return true;
        }
        if (_engine?.HandleKey(key, isDown) == true) return true;
        if (Client is null || IsWindowsShortcut(key)) return false;

        // Ctrl+V pastes the PC's clipboard: copy it into Android first, then send the keys.
        if (key == Key.V && isDown && Win32.IsKeyDown(Key.LeftCtrl, Key.RightCtrl) && !_pasting)
        {
            _pasting = true;
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText()) await Client.SetClipboardAsync(System.Windows.Clipboard.GetText());
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Clipboard busy in another app; paste whatever Android already has.
                }
                Client.SendKey(scanCode, true);
                Client.SendKey(scanCode, false);
                _pasting = false;
            });
            return true;
        }
        if (key == Key.V && !isDown && _pasting) return true;

        Client.SendKey(scanCode, isDown);
        if (isDown) _keysDown.Add(scanCode); else _keysDown.Remove(scanCode);
        return true;
    }

    /// <summary>Keys Windows itself needs: the Windows key, Alt+Tab, Alt+Esc, Alt+F4, Print Screen.</summary>
    private static bool IsWindowsShortcut(Key key) =>
        key is Key.LWin or Key.RWin or Key.PrintScreen ||
        (key is Key.Tab or Key.Escape or Key.F4 && Win32.IsKeyDown(Key.LeftAlt, Key.RightAlt)) ||
        Win32.IsKeyDown(Key.LWin, Key.RWin);

    /// <summary>Lets go of keys Android still thinks are held (e.g. after Alt+Tab mid-press).</summary>
    private void ReleaseForwardedKeys()
    {
        if (Client is not null)
            foreach (var code in _keysDown) Client.SendKey(code, false);
        _keysDown.Clear();
    }

    private void OnEditControls(object sender, RoutedEventArgs e)
    {
        if (_overlay is null)
        {
            ControlsToggle.IsChecked = false;
            return;
        }
        if (_overlay.IsEditing) _overlay.EndEdit();
        else
        {
            _engine?.ReleaseAll();
            _overlay.BeginEdit();
        }
        ControlsToggle.IsChecked = _overlay.IsEditing;
    }

    private void OnKeymapToggle(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        UpdateEngineEnabled();
        _overlay?.ShowToast(KeymapToggle.IsChecked == true ? "Game controls on" : "Game controls off: keys go straight to Android");
        Host.FocusEmulator();
    }

    /// <summary>Controls run when switched on and the user is not typing into a text field.</summary>
    private void UpdateEngineEnabled()
    {
        if (_engine is null) return;
        bool enabled = KeymapToggle.IsChecked == true && !_typing;
        if (_engine.Enabled == enabled) return;
        _engine.Enabled = enabled;
        if (!enabled) _engine.ReleaseAll();
        if (_overlay is not null) _overlay.ShowHints = enabled && _instance.Options.ShowKeyHints;
    }

    // ---- Rotation and the app on screen -----------------------------------------------

    private async Task RefreshRotationAsync()
    {
        if (Client is null) return;
        var degrees = await Client.TryGetRotationAsync();
        if (degrees is null) return;
        var turns = ScreenGeometry.QuarterTurnsFor(degrees.Value);
        if (turns == _geometry.QuarterTurns) return;
        _engine?.ReleaseAll();
        _geometry = _geometry with { QuarterTurns = turns };
        LayoutDisplay();
    }

    private async Task PollAsync()
    {
        if (_polling || _session is null || _closing) return;
        _polling = true;
        try
        {
            await RefreshRotationAsync();

            // One small dump answers all three questions (~70 ms). "dumpsys input_method" would
            // also tell whether the keyboard is up, but it prints ~800 KB and stalled games and
            // video every poll.
            var output = await _session.Adb.ShellAsync(
                "dumpsys window displays | grep -E 'mCurrentFocus=|mCurrentRotation=|mIsImeShowing='",
                TimeSpan.FromSeconds(5));
            var focus = Regex.Match(output, @"mCurrentFocus=Window\{\S+ \S+ ([A-Za-z0-9_.]+)[/}]");
            if (focus.Success && focus.Groups[1].Value != _package) SwitchApp(focus.Groups[1].Value);

            // While Android's keyboard is up the user is typing, so keys go to the text field.
            bool typing = output.Contains("mIsImeShowing=true");
            if (typing != _typing)
            {
                _typing = typing;
                UpdateEngineEnabled();
            }

            // Follow the app only when Android's own orientation changes (a game switching to
            // landscape, say). Enforcing it continuously would undo the user's Rotate button
            // before Android has caught up with the new sensor reading.
            var rotation = Regex.Match(output, @"mCurrentRotation=(?:ROTATION_)?(\d+)");
            if (rotation.Success && Client is not null)
            {
                int value = int.Parse(rotation.Groups[1].Value);
                bool changed = value != _lastAndroidRotation;
                _lastAndroidRotation = value;
                bool appSideways = (value >= 90 ? value / 90 : value) % 2 == 1;
                if (_instance.Options.AutoRotate && changed && appSideways != _geometry.IsSideways)
                {
                    await Client.SetRotationAsync(appSideways ? 90 : 0);
                    await RefreshRotationAsync();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Grpc.Core.RpcException)
        {
        }
        finally
        {
            _polling = false;
        }
    }

    private void SwitchApp(string package)
    {
        _package = package;
        if (_engine is null || _overlay is null || _overlay.IsEditing) return;
        // Launchers and system UI share the default layout; games get their own.
        var key = KeymapStore.Exists(package) ? package : KeymapStore.Default;
        var profile = KeymapStore.Load(key);
        _engine.Profile = profile;
        _overlay.SetProfile(profile, IsSystemPackage(package) ? KeymapStore.Default : package);
    }

    private static bool IsSystemPackage(string package) =>
        package.StartsWith("com.android.") || package.StartsWith("com.google.android.apps.nexuslauncher") ||
        package == "android" || package.Contains("launcher");

    // ---- Toolbar ---------------------------------------------------------------------

    /// <summary>A short, non-blocking message over the screen (used for errors during play).</summary>
    internal void ShowNotice(string message) => _overlay?.ShowToast(message);

    internal void PressDeviceKey(string key)
    {
        Client?.PressKey(key);
        Host.FocusEmulator();
    }

    private void OnBack(object sender, RoutedEventArgs e) => PressDeviceKey("GoBack");
    private void OnHome(object sender, RoutedEventArgs e) => PressDeviceKey("GoHome");
    private void OnRecents(object sender, RoutedEventArgs e) => PressDeviceKey("AppSwitch");
    private void OnVolumeUp(object sender, RoutedEventArgs e) => PressDeviceKey("AudioVolumeUp");
    private void OnVolumeDown(object sender, RoutedEventArgs e) => PressDeviceKey("AudioVolumeDown");

    /// <summary>Lists the PC's sound devices; picking one moves this instance's sound there right away.</summary>
    private void OnSoundOutput(object sender, RoutedEventArgs e)
    {
        var current = _instance.Options.OutputDevice ?? "";
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
            FontFamily = SystemFonts.MessageFontFamily,
            FontSize = 14,
        };
        foreach (var device in new[] { new AudioDevice("", "Windows default") }.Concat(AudioDevices.List(AudioDevices.Flow.Output)))
        {
            var item = new System.Windows.Controls.MenuItem { Header = device.Name, IsCheckable = true, IsChecked = device.Id == current };
            item.Click += (_, _) => SetSoundOutput(device);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SetSoundOutput(AudioDevice device)
    {
        _instance.Options.OutputDevice = device.Id.Length > 0 ? device.Id : null;
        _instance.Options.Save(_instance.Dir);
        // Applies at once while Android is making sound, otherwise as soon as it starts.
        _session?.ApplyAudioDevices();
        _overlay?.ShowToast($"Sound: {device.Name}");
        Host.FocusEmulator();
    }

    private async void OnRotate(object sender, RoutedEventArgs e)
    {
        if (Client is null) return;
        try
        {
            await Client.SetRotationAsync(_geometry.IsSideways ? 0 : 90);
            await RefreshRotationAsync();
        }
        catch (Grpc.Core.RpcException ex)
        {
            _overlay?.ShowToast($"Could not rotate: {ex.Status.Detail}");
        }
        Host.FocusEmulator();
    }

    private void OnScreenshot(object sender, RoutedEventArgs e) => TakeScreenshot();

    internal async void TakeScreenshot()
    {
        if (Client is null) return;
        try
        {
            var png = await Client.ScreenshotPngAsync();
            var path = Path.Combine(AppPaths.ScreenshotsDir, $"{_instance.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await File.WriteAllBytesAsync(path, png);
            _overlay?.ShowToast($"Screenshot saved to Pictures\\Resit\\{Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or IOException)
        {
            _overlay?.ShowToast($"Screenshot failed: {ex.Message}");
        }
        Host.FocusEmulator();
    }

    private async void OnInstallApk(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Install APK",
            Filter = "Android apps (*.apk)|*.apk",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            _overlay?.ShowToast($"Installing {Path.GetFileName(file)}…");
            var (ok, message) = await _session.Adb.InstallAsync(file);
            _overlay?.ShowToast(ok ? $"Installed {Path.GetFileName(file)}" : $"Install failed: {message}");
        }
        Host.FocusEmulator();
    }

    // ---- Fullscreen ------------------------------------------------------------------

    private void OnFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    /// <summary>
    /// Borderless fullscreen on the current monitor, taskbar included. The sidebar hides; moving
    /// the mouse to the top edge drops down a bar with the device buttons and an exit button.
    /// </summary>
    internal void ToggleFullscreen()
    {
        if (!_ready) return;
        if (!_fullscreen)
        {
            _savedState = WindowState;
            _savedBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            var monitor = Win32.MonitorBounds(_hwnd);
            var toDip = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
            var tl = toDip.Transform(new Point(monitor.Left, monitor.Top));
            // One pixel short of the monitor: a window that covers it exactly gets the screen to
            // itself ("independent flip"), and the emulator then presents without vsync, which
            // tears. Leaving one row uncovered keeps Windows compositing it in sync.
            var br = toDip.Transform(new Point(monitor.Right, monitor.Bottom - 1));

            Toolbar.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            (Left, Top, Width, Height) = (tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
            Topmost = true;
            _fullscreen = true;
            _fullscreenBar ??= new FullscreenBar(this);
            _edgeTimer.Start();
            _overlay?.ShowToast("Fullscreen · move the mouse to the top or press F11 to exit");
        }
        else
        {
            _edgeTimer.Stop();
            _fullscreenBar?.Hide();
            Topmost = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Toolbar.Visibility = Visibility.Visible;
            (Left, Top, Width, Height) = (_savedBounds.Left, _savedBounds.Top, _savedBounds.Width, _savedBounds.Height);
            WindowState = _savedState;
            _fullscreen = false;
        }
        Host.FocusEmulator();
    }

    /// <summary>Shows the drop-down bar while the mouse is at the top edge (or over the bar).</summary>
    private void UpdateFullscreenBar()
    {
        if (!_fullscreen || _fullscreenBar is null) return;
        Win32.GetCursorPos(out var cursor);
        var monitor = Win32.MonitorBounds(_hwnd);
        bool onMonitor = cursor.X >= monitor.Left && cursor.X < monitor.Right;
        double barHeightPx = Math.Max(60, _fullscreenBar.ActualHeight * (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1) + 20);

        if (onMonitor && cursor.Y <= monitor.Top + 2)
        {
            if (!_fullscreenBar.IsVisible)
            {
                _fullscreenBar.Show();
                _fullscreenBar.UpdateLayout();
                _fullscreenBar.Left = Left + (Width - _fullscreenBar.ActualWidth) / 2;
                _fullscreenBar.Top = Top;
            }
            _barHideAt = DateTime.UtcNow.AddSeconds(1.2);
        }
        else if (_fullscreenBar.IsVisible)
        {
            if (onMonitor && cursor.Y <= monitor.Top + barHeightPx) _barHideAt = DateTime.UtcNow.AddSeconds(1.2);
            else if (DateTime.UtcNow > _barHideAt) _fullscreenBar.Hide();
        }
    }

    private async void OnExtendedControls(object sender, RoutedEventArgs e)
    {
        if (Client is not null) await Client.ShowExtendedControlsAsync();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_instance, running: true) { Owner = this }.ShowDialog();
        _instance.Reload();
        _session?.ApplyAudioDevices();   // device choices apply live; everything else on next start
        if (_overlay is not null) _overlay.ShowHints = _instance.Options.ShowKeyHints && _engine?.Enabled != false;
        Host.FocusEmulator();
    }

    private void OnPower(object sender, RoutedEventArgs e) => Close();

    // ---- Shutdown --------------------------------------------------------------------

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing || _session is null || _session.HasExited) return;
        e.Cancel = true;
        _closing = true;
        _pollTimer.Stop();
        _fitTimer.Stop();
        _edgeTimer.Stop();
        _fullscreenBar?.Hide();
        _overlay?.Hide();
        Host.Visibility = Visibility.Hidden;
        LoadingPanel.Visibility = Visibility.Visible;
        ShowProgress(_instance.Options.QuickBoot ? "Saving state and shutting down" : "Shutting down", 100, "");
        LoadingBar.IsIndeterminate = true;
        LoadingPercent.Text = "";
        await _session.StopAsync();
        if (!_closed) Close();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _cts.Cancel();
        _pollTimer.Stop();
        _fitTimer.Stop();
        _edgeTimer.Stop();
        _hook?.Dispose();
        _overlay?.Close();
        _fullscreenBar?.Close();
        _open.Remove(_instance.Name);
        SessionsChanged?.Invoke(null, EventArgs.Empty);
        if (_session is not null) await _session.DisposeAsync();
    }
}
