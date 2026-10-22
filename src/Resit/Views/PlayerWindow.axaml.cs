using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Resit.Core;
using Resit.Platform;

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

    private static NativeWindows Native => NativeWindows.Current;

    private readonly SdkLocation _sdk = null!;
    private readonly Instance _instance = null!;
    private readonly StartOptions _startOptions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _fitTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private EmulatorSession? _session;
    private KeymapOverlay? _overlay;
    private KeymapEngine? _engine;
    private KeyCapture? _keys;
    private ScreenGeometry _geometry;
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
    private PixelPoint _savedPosition;
    private Size _savedSize;
    private DateTime _barHideAt;

    // For the XAML previewer.
    public PlayerWindow() => InitializeComponent();

    private PlayerWindow(SdkLocation sdk, Instance instance, StartOptions options)
    {
        InitializeComponent();
        _sdk = sdk;
        _instance = instance;
        _startOptions = options;
        _geometry = new ScreenGeometry(instance.Width, instance.Height, 0);
        Title = $"{instance.DisplayName} · Resit";
        ScreenshotButton.SetValue(ToolTip.TipProperty, $"Screenshot (saved to {Path.Combine("Pictures", "Resit")})");
        SizeToScreen();

        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Closed += OnClosed;
        Deactivated += (_, _) =>
        {
            _engine?.ReleaseAll();
            ReleaseForwardedKeys();
        };
        PositionChanged += (_, _) => UpdateOverlayBounds();
        Resized += (_, _) => UpdateOverlayBounds();
        _fitTimer.Tick += (_, _) => KeepEmbedded();
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _edgeTimer.Tick += (_, _) => UpdateFullscreenBar();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastPopup.IsOpen = false;
        };
    }

    private EmulatorClient? Client => _session?.Client;

    private IntPtr Handle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// <summary>Opens as large as fits in 85% of the screen, keeping the device's aspect ratio.</summary>
    private void SizeToScreen()
    {
        if (Screens.Primary is not { } screen) return;
        double scale = screen.Scaling;
        var work = screen.WorkingArea;
        double workW = work.Width / scale, workH = work.Height / scale;
        double chromeW = ToolbarWidth + 16, chromeH = 32 + 16;
        double maxW = workW * 0.85 - chromeW, maxH = workH * 0.85 - chromeH;
        double w = maxW, h = w / _geometry.ShownAspect;
        if (h > maxH) { h = maxH; w = h * _geometry.ShownAspect; }
        Width = w + chromeW;
        Height = h + chromeH;
        Position = new PixelPoint(work.X + (int)((work.Width - Width * scale) / 2), work.Y + (int)((work.Height - Height * scale) / 2));
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
            _session.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
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
            _session.HideToolbars();
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
            Host.IsVisible = true;
            LoadingPanel.IsVisible = false;
            NudgeLater(0.3, 1.5, 4);
            Title = $"{_instance.DisplayName} · Resit";

            _engine = new KeymapEngine(Client!, () => _geometry) { Profile = KeymapStore.Load(KeymapStore.Default) };
            _overlay = new KeymapOverlay
            {
                ShowHints = _instance.Options.ShowKeyHints,
                Screenshot = ScreenshotBitmapAsync,
            };
            _overlay.SetProfile(_engine.Profile, KeymapStore.Default);
            _overlay.Saved += profile =>
            {
                _engine.Profile = profile;
                ShowNotice(_overlay.Package == KeymapStore.Default ? "Controls saved" : $"Controls saved for {_overlay.Package}");
            };
            _overlay.EditingEnded += () =>
            {
                ControlsToggle.IsChecked = false;
                Activate();
                Host.FocusEmulator();
            };
            if (KeymapOverlay.ShowsDuringPlay) _overlay.Show(this);
            UpdateOverlayBounds();
            _keys = KeyCapture.Start(Handle, IsGameInputActive, OnKey);
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
            Host.IsVisible = false;
            LoadingPanel.IsVisible = true;
            ShowProgress("Could not start the instance", 0, ex.Message);
            LoadingPercent.Text = "";
        }
    }

    // ---- Keeping the embedded window in shape ---------------------------------------

    private void KeepEmbedded()
    {
        Host.KeepFitted();
        _session?.HideToolbars();
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

    private void OnDisplayAreaSizeChanged(object? sender, SizeChangedEventArgs e) => LayoutDisplay();

    /// <summary>Sizes the screen to the largest rectangle with the device's aspect ratio.</summary>
    private void LayoutDisplay()
    {
        double w = DisplayArea.Bounds.Width, h = DisplayArea.Bounds.Height;
        if (w < 1 || h < 1) return;
        double aspect = _geometry.ShownAspect;
        if (w / h > aspect) w = h * aspect; else h = w / aspect;
        Host.Width = Math.Floor(w);
        Host.Height = Math.Floor(h);
        Dispatcher.UIThread.Post(UpdateOverlayBounds, DispatcherPriority.Render);
    }

    /// <summary>Lays the overlay exactly over the emulator's screen (which the emulator centres itself).</summary>
    private void UpdateOverlayBounds()
    {
        if (_overlay is null || WindowState == WindowState.Minimized || !Native.IsAlive(Host.EmulatorWindow)) return;
        var r = Native.GetScreenBounds(Host.EmulatorWindow);
        if (r.Width <= 0 || r.Height <= 0) return;
        double scale = RenderScaling;
        if (_overlay.Position != r.Position) _overlay.Position = r.Position;
        if (Math.Abs(_overlay.Width - r.Width / scale) > 0.5) _overlay.Width = r.Width / scale;
        if (Math.Abs(_overlay.Height - r.Height / scale) > 0.5) _overlay.Height = r.Height / scale;
    }

    // ---- Game controls ---------------------------------------------------------------

    private bool IsGameInputActive() =>
        _engine is not null && _overlay is { IsEditing: false } && Native.IsForeground(Handle);

    /// <summary>
    /// Every key pressed while the player is in front. Game controls get first pick; OS
    /// shortcuts stay with the OS; everything else is typed into Android over gRPC. (The
    /// embedded emulator window cannot take keyboard input itself: Qt only accepts keys while
    /// its own top-level window is active, and inside Resit it never is.)
    /// </summary>
    private bool OnKey(KeyStroke stroke)
    {
        var key = stroke.Key;
        bool isDown = stroke.IsDown;
        if (key == Key.F11)
        {
            if (isDown) Dispatcher.UIThread.Post(ToggleFullscreen);
            return true;
        }
        if (_engine?.HandleKey(key, isDown) == true) return true;
        if (Client is null || IsSystemShortcut(stroke)) return false;

        // Ctrl+V pastes the PC's clipboard: copy it into Android first, then send the keys.
        if (key == Key.V && isDown && stroke.Modifiers.HasFlag(KeyModifiers.Control) && !_pasting)
        {
            _pasting = true;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } text) await Client.SetClipboardAsync(text);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Clipboard busy in another app; paste whatever Android already has.
                    Log.Write($"clipboard: {ex.Message}");
                }
                Client.SendKey(stroke.Code, true);
                Client.SendKey(stroke.Code, false);
                _pasting = false;
            });
            return true;
        }
        if (key == Key.V && !isDown && _pasting) return true;

        Client.SendKey(stroke.Code, isDown);
        if (isDown) _keysDown.Add(stroke.Code); else _keysDown.Remove(stroke.Code);
        return true;
    }

    /// <summary>Keys the OS needs: the Windows/Super key, Alt+Tab, Alt+Esc, Alt+F4, Print Screen.</summary>
    private static bool IsSystemShortcut(KeyStroke s) =>
        s.Key is Key.LWin or Key.RWin or Key.PrintScreen ||
        (s.Key is Key.Tab or Key.Escape or Key.F4 && s.Modifiers.HasFlag(KeyModifiers.Alt)) ||
        s.Modifiers.HasFlag(KeyModifiers.Meta);

    /// <summary>Lets go of keys Android still thinks are held (e.g. after Alt+Tab mid-press).</summary>
    private void ReleaseForwardedKeys()
    {
        if (Client is not null)
            foreach (var code in _keysDown) Client.SendKey(code, false);
        _keysDown.Clear();
    }

    private void OnEditControls(object? sender, RoutedEventArgs e)
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
            UpdateOverlayBounds();
            _overlay.BeginEdit(this);
        }
        ControlsToggle.IsChecked = _overlay.IsEditing;
    }

    private void OnKeymapToggle(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        UpdateEngineEnabled();
        ShowNotice(KeymapToggle.IsChecked == true ? "Game controls on" : "Game controls off: keys go straight to Android");
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
    internal void ShowNotice(string message)
    {
        ToastText.Text = message;
        ToastPopup.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    internal void PressDeviceKey(string key)
    {
        Client?.PressKey(key);
        Host.FocusEmulator();
    }

    private void OnBack(object? sender, RoutedEventArgs e) => PressDeviceKey("GoBack");
    private void OnHome(object? sender, RoutedEventArgs e) => PressDeviceKey("GoHome");
    private void OnRecents(object? sender, RoutedEventArgs e) => PressDeviceKey("AppSwitch");
    private void OnVolumeUp(object? sender, RoutedEventArgs e) => PressDeviceKey("AudioVolumeUp");
    private void OnVolumeDown(object? sender, RoutedEventArgs e) => PressDeviceKey("AudioVolumeDown");

    /// <summary>Lists the PC's sound devices; picking one moves this instance's sound there right away.</summary>
    private void OnSoundOutput(object? sender, RoutedEventArgs e)
    {
        var current = _instance.Options.OutputDevice ?? "";
        var menu = new ContextMenu { Placement = PlacementMode.Left };
        foreach (var device in new[] { new AudioDevice("", OS.DefaultDeviceName) }.Concat(AudioDevices.List(Flow.Output)))
        {
            var item = new MenuItem { Header = device.Name, ToggleType = MenuItemToggleType.Radio, IsChecked = device.Id == current };
            item.Click += (_, _) => SetSoundOutput(device);
            menu.Items.Add(item);
        }
        menu.Open((Control)sender!);
    }

    private void SetSoundOutput(AudioDevice device)
    {
        _instance.Options.OutputDevice = device.Id.Length > 0 ? device.Id : null;
        _instance.Options.Save(_instance.Dir);
        // Applies at once while Android is making sound, otherwise as soon as it starts.
        _session?.ApplyAudioDevices();
        ShowNotice($"Sound: {device.Name}");
        Host.FocusEmulator();
    }

    private async void OnRotate(object? sender, RoutedEventArgs e)
    {
        if (Client is null) return;
        try
        {
            await Client.SetRotationAsync(_geometry.IsSideways ? 0 : 90);
            await RefreshRotationAsync();
        }
        catch (Grpc.Core.RpcException ex)
        {
            ShowNotice($"Could not rotate: {ex.Status.Detail}");
        }
        Host.FocusEmulator();
    }

    private void OnScreenshot(object? sender, RoutedEventArgs e) => TakeScreenshot();

    private async void TakeScreenshot()
    {
        if (Client is null) return;
        try
        {
            var png = await Client.ScreenshotPngAsync();
            var path = Path.Combine(AppPaths.ScreenshotsDir, $"{_instance.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await File.WriteAllBytesAsync(path, png);
            ShowNotice($"Screenshot saved to {Path.Combine("Pictures", "Resit", Path.GetFileName(path))}");
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or IOException)
        {
            ShowNotice($"Screenshot failed: {ex.Message}");
        }
        Host.FocusEmulator();
    }

    /// <summary>The screen as a bitmap, for the controls editor on desktops without see-through windows.</summary>
    private async Task<Bitmap?> ScreenshotBitmapAsync()
    {
        if (Client is null) return null;
        try
        {
            using var stream = new MemoryStream(await Client.ScreenshotPngAsync());
            return new Bitmap(stream);
        }
        catch (Grpc.Core.RpcException)
        {
            return null;
        }
    }

    private async void OnInstallApk(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install APK",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Android apps") { Patterns = ["*.apk"] }],
        });
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;
            ShowNotice($"Installing {Path.GetFileName(path)}…");
            var (ok, message) = await _session.Adb.InstallAsync(path);
            ShowNotice(ok ? $"Installed {Path.GetFileName(path)}" : $"Install failed: {message}");
        }
        Host.FocusEmulator();
    }

    // ---- Fullscreen ------------------------------------------------------------------

    private void OnFullscreen(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    /// <summary>
    /// Fullscreen on the current monitor, taskbar included. The sidebar hides; moving the mouse
    /// to the top edge drops down a bar with the device buttons and an exit button.
    /// </summary>
    internal void ToggleFullscreen()
    {
        if (!_ready) return;
        if (!_fullscreen)
        {
            _savedState = WindowState;
            _savedPosition = Position;
            _savedSize = new Size(Width, Height);
            Toolbar.IsVisible = false;

            if (OS.IsWindows && Screens.ScreenFromWindow(this) is { } screen)
            {
                // Borderless, one pixel short of the monitor: a window that covers it exactly gets
                // the screen to itself ("independent flip"), and the emulator then presents without
                // vsync, which tears. Leaving one row uncovered keeps Windows compositing it in sync.
                var m = screen.Bounds;
                WindowState = WindowState.Normal;
                WindowDecorations = WindowDecorations.None;
                CanResize = false;
                Position = m.Position;
                Width = m.Width / screen.Scaling;
                Height = (m.Height - 1) / screen.Scaling;
                Topmost = true;
            }
            else
            {
                WindowState = WindowState.FullScreen;
            }
            _fullscreen = true;
            _edgeTimer.Start();
            ShowNotice("Fullscreen · move the mouse to the top or press F11 to exit");
        }
        else
        {
            _edgeTimer.Stop();
            FullscreenBar.IsOpen = false;
            if (OS.IsWindows)
            {
                Topmost = false;
                WindowDecorations = WindowDecorations.Full;
                CanResize = true;
                Position = _savedPosition;
                (Width, Height) = (_savedSize.Width, _savedSize.Height);
            }
            Toolbar.IsVisible = true;
            WindowState = _savedState;
            _fullscreen = false;
        }
        Host.FocusEmulator();
    }

    /// <summary>Shows the drop-down bar while the mouse is at the top edge (or over the bar).</summary>
    private void UpdateFullscreenBar()
    {
        if (!_fullscreen || Screens.ScreenFromWindow(this) is not { } screen) return;
        var cursor = Native.CursorPosition();
        var monitor = screen.Bounds;
        bool onMonitor = cursor.X >= monitor.X && cursor.X < monitor.Right;
        double barHeightPx = Math.Max(60, (FullscreenBar.Child?.Bounds.Height ?? 52) * screen.Scaling + 20);

        if (onMonitor && cursor.Y <= monitor.Y + 2)
        {
            FullscreenBar.IsOpen = true;
            _barHideAt = DateTime.UtcNow.AddSeconds(1.2);
        }
        else if (FullscreenBar.IsOpen)
        {
            if (onMonitor && cursor.Y <= monitor.Y + barHeightPx) _barHideAt = DateTime.UtcNow.AddSeconds(1.2);
            else if (DateTime.UtcNow > _barHideAt) FullscreenBar.IsOpen = false;
        }
    }

    private async void OnExtendedControls(object? sender, RoutedEventArgs e)
    {
        if (Client is not null) await Client.ShowExtendedControlsAsync();
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        await new SettingsWindow(_instance, running: true).ShowDialog<bool>(this);
        _instance.Reload();
        _session?.ApplyAudioDevices();   // device choices apply live; everything else on next start
        if (_overlay is not null) _overlay.ShowHints = _instance.Options.ShowKeyHints && _engine?.Enabled != false;
        Host.FocusEmulator();
    }

    private void OnPower(object? sender, RoutedEventArgs e) => Close();

    // ---- Shutdown --------------------------------------------------------------------

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing || _session is null || _session.HasExited) return;
        e.Cancel = true;
        _closing = true;
        _pollTimer.Stop();
        _fitTimer.Stop();
        _edgeTimer.Stop();
        FullscreenBar.IsOpen = false;
        ToastPopup.IsOpen = false;
        _overlay?.Hide();
        Host.IsVisible = false;
        LoadingPanel.IsVisible = true;
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
        _toastTimer.Stop();
        _keys?.Dispose();
        _overlay?.Close();
        _open.Remove(_instance.Name);
        SessionsChanged?.Invoke(null, EventArgs.Empty);
        if (_session is not null) await _session.DisposeAsync();
    }
}
