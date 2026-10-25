using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Resit.Core;

namespace Resit.Views;

public sealed class InstanceItem(Instance instance) : INotifyPropertyChanged
{
    private bool _running;

    public Instance Instance { get; } = instance;
    public string DisplayName => Instance.DisplayName;
    public string ImageSummary => Instance.ImageSummary;
    public string Summary => Instance.Summary;

    public bool IsRunning
    {
        get => _running;
        set
        {
            if (_running == value) return;
            _running = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusBackground));
            OnPropertyChanged(nameof(StatusForeground));
            OnPropertyChanged(nameof(PlayLabel));
            OnPropertyChanged(nameof(PlayIcon));
        }
    }

    public string Status => IsRunning ? "Running" : "Stopped";
    public IBrush StatusBackground => IsRunning ? new SolidColorBrush(Color.FromArgb(0x26, 0x3D, 0xD6, 0x8C)) : new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    public IBrush? StatusForeground => Resource<IBrush>(IsRunning ? "Success" : "TextSecondary");
    public string PlayLabel => IsRunning ? "Open" : "Play";
    public Geometry? PlayIcon => Resource<Geometry>(IsRunning ? "IconOpen" : "IconPlay");

    private static T? Resource<T>(string key) where T : class =>
        Application.Current!.TryFindResource(key, out var value) ? value as T : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    private readonly SdkLocation _sdk = SdkLocation.Detect();
    private readonly InstanceStore _store;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private List<InstanceItem> _items = [];
    private bool _sdkInstalled;

    private enum Accel { Unknown, Ready, Off, Enabling, RestartNeeded, Failed }
    private Accel _accel;
    /// <summary>What the banner says about acceleration: progress while turning it on, or why that failed.</summary>
    private string _accelDetail = "";
    /// <summary>Acceleration is turned on by itself once per run; after that only when the user asks.</summary>
    private bool _autoEnableTried;

    public MainWindow()
    {
        InitializeComponent();
        _store = new InstanceStore(_sdk);
        _sdkInstalled = _sdk.IsInstalled;
        var host = HostInfo.Current;
        HostText.Text = $"{host.CpuName} · {host.PhysicalCores}C/{host.LogicalCores}T · {host.RamMB / 1024.0:0} GB"
            + (host.Gpus.Count > 0 ? $" · {host.Gpus[0]}" : "");
        Opened += async (_, _) =>
        {
            Refresh();
            UpdateBanner();
            if (!_sdk.IsInstalled) StartEmulatorSetup();
            await CheckAccelerationAsync();
            if (await Task.Run(() => HostInfo.IsOnHardDrive(_sdk.AvdHome))) DiskWarning.IsVisible = true;
        };
        _statusTimer.Tick += (_, _) => UpdateRunningState();
        _statusTimer.Start();
        PlayerWindow.SessionsChanged += OnSessionsChanged;
        SdkSetup.StatusChanged += UpdateBanner;
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            PlayerWindow.SessionsChanged -= OnSessionsChanged;
            SdkSetup.StatusChanged -= UpdateBanner;
        };
    }

    private void OnSessionsChanged(object? sender, EventArgs e) => UpdateRunningState();

    private void Refresh()
    {
        _items = _store.List().Select(i => new InstanceItem(i)).ToList();
        InstanceList.ItemsSource = _items;
        UpdateRunningState();
        EmptyState.IsVisible = _items.Count == 0;
    }

    /// <summary>
    /// First start: downloads the emulator in the background. Instances can be created
    /// meanwhile; the timer notices when the emulator is there.
    /// </summary>
    private void StartEmulatorSetup()
    {
        if (!SdkSetup.IsRunning) Log.Write($"SDK not found at {_sdk.SdkRoot}: {_sdk.MissingReason()} Downloading it.");
        _ = SdkSetup.RunAsync(_sdk);
    }

    /// <summary>
    /// The banner over the list shows what is left before playing: the emulator downloading (or
    /// why that failed), then hardware acceleration being turned on, needing a restart, or
    /// failing. Hidden once nothing is left.
    /// </summary>
    private void UpdateBanner()
    {
        if (SdkSetup.Status is { } s && (s.Running || s.Error is not null))
        {
            bool failed = s.Error is not null;
            ShowBanner(failed ? "IconWarning" : "IconInstall", failed,
                failed ? "Could not download the Android Emulator" : "Setting up the Android Emulator",
                s.Error ?? s.Detail, s.Running ? s.Percent ?? -1 : null,
                s.Running ? "A one-time download from Google, under the Android SDK License Agreement. You can already create an instance: the Android version you pick downloads right after." : "",
                failed ? "Try again" : null);
            return;
        }
        switch (_accel)
        {
            case Accel.Enabling:
                ShowBanner("IconPower", false, "Turning on hardware acceleration", _accelDetail, -1,
                    "The emulator needs it to run Android. Windows asks for your permission once.", null);
                break;
            case Accel.RestartNeeded:
                ShowBanner("IconPower", false, "Restart your PC to finish",
                    "Hardware acceleration is turned on and starts working after a restart.", null, "", "Restart now");
                break;
            case Accel.Off when OperatingSystem.IsWindows():
                ShowBanner("IconWarning", true, "Hardware acceleration is off",
                    _accelDetail is "" ? "Instances cannot start without it." : _accelDetail, null, "", "Turn on");
                break;
            case Accel.Off:
                ShowBanner("IconWarning", true, "Hardware acceleration is off", "Instances cannot start until KVM is usable.", null, "", "How to fix");
                break;
            case Accel.Failed:
                ShowBanner("IconWarning", true, "Could not turn on hardware acceleration", _accelDetail, null, "", "Try again");
                break;
            default:
                Banner.IsVisible = false;
                break;
        }
    }

    /// <summary>A null percent hides the progress bar; a negative one shows it moving (length unknown).</summary>
    private void ShowBanner(string icon, bool warning, string title, string detail, double? percent, string hint, string? action)
    {
        Banner.IsVisible = true;
        if (this.TryFindResource(icon, out var geometry)) BannerIcon.Data = geometry as Geometry;
        if (this.TryFindResource(warning ? "Warning" : "AccentText", out var brush)) BannerIcon.Foreground = brush as IBrush;
        BannerTitle.Text = title;
        BannerDetail.Text = detail;
        BannerDetail.IsVisible = detail.Length > 0;
        BannerBar.IsVisible = percent is not null;
        BannerBar.IsIndeterminate = percent < 0;
        BannerBar.Value = Math.Max(0, percent ?? 0);
        BannerPercent.Text = percent >= 0 ? $"{percent:0}%" : "";
        BannerHint.Text = hint;
        BannerHint.IsVisible = hint.Length > 0;
        BannerAction.Content = action;
        BannerAction.IsVisible = action is not null;
    }

    private async void OnBannerAction(object? sender, RoutedEventArgs e)
    {
        if (SdkSetup.Status?.Error is not null) StartEmulatorSetup();
        else if (_accel == Accel.RestartNeeded) await RestartAsync();
        else await EnableAccelerationAsync();
    }

    private void UpdateRunningState()
    {
        // The emulator can appear after start-up (the background setup finished, a drive came
        // back, files were being moved) or go away, so keep checking.
        if (_sdk.IsInstalled != _sdkInstalled)
        {
            _sdkInstalled = _sdk.IsInstalled;
            _ = CheckAccelerationAsync();
        }
        var running = RunningEmulator.List().Select(r => r.AvdName).ToHashSet();
        foreach (var item in _items)
            item.IsRunning = running.Contains(item.Instance.Name) || PlayerWindow.IsOpen(item.Instance.Name);
    }

    private async Task CheckAccelerationAsync()
    {
        if (_accel == Accel.Enabling) return;   // EnableAccelerationAsync checks again when it is done
        _accel = Accel.Unknown;
        _accelDetail = "";
        SetAccelFooter("TextSecondary", _sdk.IsInstalled ? "Checking hardware acceleration…" : "Hardware acceleration is checked once the emulator is installed");
        if (_sdk.IsInstalled)
        {
            try
            {
                var (ok, line) = await Acceleration.CheckAsync(_sdk);
                _accel = ok ? Accel.Ready : OperatingSystem.IsWindows() && Acceleration.RestartPending ? Accel.RestartNeeded : Accel.Off;
                SetAccelFooter(ok ? "Success" : "Warning",
                    ok ? $"Hardware acceleration ready · {line}" :
                    _accel == Accel.RestartNeeded ? "Hardware acceleration works after a restart" : "Hardware acceleration is off, so instances cannot start");
                if (_accel == Accel.Off && Acceleration.RestartedWithoutEffect)
                    _accelDetail = "It is still off after the restart. Virtualization may be turned off in the BIOS/UEFI (Intel VT-x or AMD SVM).";
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception or IOException)
            {
                SetAccelFooter("TextSecondary", "Could not check hardware acceleration");
            }
        }
        UpdateBanner();

        // Turn it on by itself, once, so all that is left to the user is Windows' permission
        // prompt and maybe a restart. Not again after a restart that did not help: the banner
        // explains instead of prompting on every start.
        if (_accel == Accel.Off && OperatingSystem.IsWindows() && !_autoEnableTried && !Acceleration.RestartedWithoutEffect)
        {
            _autoEnableTried = true;
            await EnableAccelerationAsync();
        }
    }

    private void SetAccelFooter(string brush, string text)
    {
        if (this.TryFindResource(brush, out var fill)) AccelDot.Fill = fill as IBrush;
        AccelText.Text = text;
    }

    /// <summary>
    /// Turns hardware acceleration on. On Windows this runs by itself (Windows asks for admin
    /// rights) and ends ready or waiting for a restart; on Linux it explains the KVM steps.
    /// </summary>
    private async Task EnableAccelerationAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            await Dialogs.ShowAsync(this, "Hardware acceleration",
                "The emulator needs KVM. Turn on virtualization (Intel VT-x or AMD SVM) in your BIOS, then give your " +
                "user access to KVM with the command below. Log out and back in afterwards.",
                "sudo usermod -aG kvm $USER");
            await CheckAccelerationAsync();
            return;
        }
        if (_accel == Accel.Enabling) return;
        _accel = Accel.Enabling;
        _accelDetail = "Starting…";
        UpdateBanner();
        SetAccelFooter("TextSecondary", "Turning on hardware acceleration…");

        EnableResult result;
        string? error;
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (_accel != Accel.Enabling) return;   // a late report after the result
                _accelDetail = text;
                UpdateBanner();
            });
            (result, error) = await Acceleration.EnableOnWindowsAsync(_sdk, progress);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"turning on acceleration failed: {ex}");
            (result, error) = (EnableResult.Failed, ex.Message);
        }

        _accel = result switch
        {
            EnableResult.RestartNeeded => Accel.RestartNeeded,
            EnableResult.Declined => Accel.Off,
            EnableResult.Failed => Accel.Failed,
            _ => Accel.Unknown,
        };
        _accelDetail = result == EnableResult.Declined ? "Windows did not get permission, so it is still off." : error ?? "";
        if (result == EnableResult.Ready)
        {
            await CheckAccelerationAsync();
            return;
        }
        SetAccelFooter("Warning", result == EnableResult.RestartNeeded ? "Hardware acceleration works after a restart" : "Hardware acceleration is off, so instances cannot start");
        UpdateBanner();
    }

    private async Task RestartAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (await Dialogs.ConfirmAsync(this, "Restart now",
                "Hardware acceleration starts working after Windows restarts. Restart now? Save your work in other apps first.", "Restart"))
            Acceleration.RestartWindows();
    }

    /// <summary>
    /// True when an instance can start. Otherwise explains why and offers the fix: the emulator
    /// is still downloading or missing, or hardware acceleration is off (the emulator refuses to
    /// run x86_64 Android without it).
    /// </summary>
    private async Task<bool> ReadyToPlayAsync()
    {
        if (!_sdk.IsInstalled)
        {
            if (SdkSetup.IsRunning)
                await Dialogs.ShowAsync(this, "Almost ready", "Resit is still downloading the Android Emulator. Press Play again when the bar at the top is done.");
            else if (await Dialogs.ConfirmAsync(this, "Android Emulator missing", "The Android Emulator is not installed. Download it now?", "Download"))
                StartEmulatorSetup();
            return false;
        }
        if (_accel is Accel.Off or Accel.Failed) await CheckAccelerationAsync();   // it may have been fixed meanwhile
        switch (_accel)
        {
            case Accel.Enabling:
                await Dialogs.ShowAsync(this, "Almost ready", "Resit is turning on hardware acceleration. Press Play again when it is done.");
                return false;
            case Accel.RestartNeeded:
                await RestartAsync();
                return false;
            case Accel.Off or Accel.Failed:
                if (await Dialogs.ConfirmAsync(this, "Hardware acceleration is off",
                        "The emulator needs hardware acceleration to run Android, and it is off on this PC. Turn it on now?", "Turn on"))
                    await EnableAccelerationAsync();
                return _accel == Accel.Ready;
            default:
                return true;
        }
    }

    private static Instance InstanceOf(object? sender) => ((InstanceItem)((Control)sender!).Tag!).Instance;

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        var instance = InstanceOf(sender);
        // An open instance only comes to the front.
        if (PlayerWindow.IsOpen(instance.Name) || await ReadyToPlayAsync())
            PlayerWindow.Open(_sdk, instance, new StartOptions());
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var instance = InstanceOf(sender);
        if (await new SettingsWindow(instance, PlayerWindow.IsOpen(instance.Name)).ShowDialog<bool>(this)) Refresh();
    }

    private void OnMore(object? sender, RoutedEventArgs e)
    {
        var instance = InstanceOf(sender);
        bool running = PlayerWindow.IsOpen(instance.Name) || RunningEmulator.List().Any(r => r.AvdName == instance.Name);
        var menu = new ContextMenu();

        void Item(string header, string icon, Action action, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = enabled,
                Icon = new PathIcon { Data = this.TryFindResource(icon, out var g) ? g as Geometry : null },
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Item("Cold boot", "IconColdBoot", async () =>
        {
            if (await ReadyToPlayAsync()) PlayerWindow.Open(_sdk, instance, new StartOptions(ColdBoot: true));
        }, !running);
        Item("Wipe data…", "IconWipe", async () =>
        {
            if (!await Dialogs.ConfirmAsync(this, "Wipe data", $"Erase all apps and data in {instance.DisplayName}? This cannot be undone.", "Wipe")) return;
            InstanceStore.WipeData(instance);
        }, !running);
        Item("Open folder", "IconFolder", () => OpenPath(instance.Dir));
        Item("Open log", "IconLog", () =>
        {
            var log = Path.Combine(instance.Dir, "resit-emulator.log");
            if (File.Exists(log)) OpenPath(log);
        });
        menu.Items.Add(new Separator());
        Item("Delete…", "IconDelete", async () =>
        {
            var image = _store.ImageOnlyUsedBy(instance);
            var also = image is null ? "" :
                $" Android {image.AndroidVersion} ({image.TagDisplay}, {_store.SizeOf(image) / (1024.0 * 1024 * 1024):0.0} GB) is removed too, since no other instance uses it.";
            if (!await Dialogs.ConfirmAsync(this, "Delete instance", $"Delete {instance.DisplayName} and everything in it?{also}", "Delete")) return;
            try
            {
                await _store.DeleteAsync(instance);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"delete failed: {ex}");
                await Dialogs.ShowAsync(this, "Delete instance",
                    $"Some files could not be deleted. Close anything that uses them (a file manager, another emulator) and delete again.\n\n{ex.Message}");
            }
            Refresh();
        }, !running);
        menu.Open((Control)sender!);
    }

    /// <summary>Opens a folder or file with the system's default app (Explorer, Files, a text editor).</summary>
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private async void OnNewInstance(object? sender, RoutedEventArgs e)
    {
        if (await new NewInstanceWindow(_store).ShowDialog<bool>(this)) Refresh();
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Refresh();
}
