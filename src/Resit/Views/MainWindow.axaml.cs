using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Resit.Core;
using Resit.Platform;

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

    public MainWindow()
    {
        InitializeComponent();
        _store = new InstanceStore(_sdk);
        var host = HostInfo.Current;
        HostText.Text = $"{host.CpuName} · {host.PhysicalCores}C/{host.LogicalCores}T · {host.RamMB / 1024.0:0} GB"
            + (host.Gpus.Count > 0 ? $" · {host.Gpus[0]}" : "");
        Opened += async (_, _) =>
        {
            Refresh();
            await CheckAccelerationAsync();
            if (await Task.Run(() => HostInfo.IsOnHardDrive(_sdk.AvdHome))) DiskWarning.IsVisible = true;
        };
        _statusTimer.Tick += (_, _) => UpdateRunningState();
        _statusTimer.Start();
        PlayerWindow.SessionsChanged += OnSessionsChanged;
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            PlayerWindow.SessionsChanged -= OnSessionsChanged;
        };
    }

    private void OnSessionsChanged(object? sender, EventArgs e) => UpdateRunningState();

    private void Refresh()
    {
        if (!_sdk.IsInstalled)
        {
            InstanceList.ItemsSource = null;
            var reason = _sdk.MissingReason();
            Log.Write($"SDK not found at {_sdk.SdkRoot}: {reason}");
            ShowEmpty("IconWarning", "Android SDK not found",
                $"Resit needs the Android Emulator. It looked in {_sdk.SdkRoot}. {reason} Run setup to download it (about 3 GB).",
                SdkLocation.FindUpwards(OS.SetupScript) is null ? null : "Run setup");
            NewButton.IsEnabled = false;
            return;
        }

        NewButton.IsEnabled = true;
        _items = _store.List().Select(i => new InstanceItem(i)).ToList();
        InstanceList.ItemsSource = _items;
        UpdateRunningState();
        if (_items.Count == 0)
            ShowEmpty("IconPhone", "No instances yet", "Create an instance to start playing. Resit sizes it to your PC automatically.", "Create instance");
        else
            EmptyState.IsVisible = false;
    }

    private void ShowEmpty(string icon, string title, string text, string? action)
    {
        if (this.TryFindResource(icon, out var geometry)) EmptyIcon.Data = geometry as Geometry;
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyAction.Content = action;
        EmptyAction.IsVisible = action is not null;
        EmptyState.IsVisible = true;
    }

    private void UpdateRunningState()
    {
        // The SDK can appear after start-up (setup finished, a drive came back, files were
        // being moved), so keep checking instead of showing "not found" forever.
        if (_sdk.IsInstalled != NewButton.IsEnabled)
        {
            Refresh();
            return;
        }
        var running = RunningEmulator.List().Select(r => r.AvdName).ToHashSet();
        foreach (var item in _items)
            item.IsRunning = running.Contains(item.Instance.Name) || PlayerWindow.IsOpen(item.Instance.Name);
    }

    private async Task CheckAccelerationAsync()
    {
        if (!_sdk.IsInstalled)
        {
            AccelText.Text = "Emulator not installed";
            return;
        }
        try
        {
            var (_, output) = await ProcessRunner.RunAsync(_sdk.Emulator, ["-accel-check"], TimeSpan.FromSeconds(20));
            var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("usable") || l.Contains("installed"));
            bool ok = output.Contains("is installed and usable");
            if (this.TryFindResource(ok ? "Success" : "Warning", out var brush)) AccelDot.Fill = brush as IBrush;
            AccelText.Text = ok ? $"Hardware acceleration ready · {line}" : "Hardware acceleration is off. The emulator will be very slow.";
            AccelFix.IsVisible = !ok;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception)
        {
            AccelText.Text = "Could not check hardware acceleration";
        }
    }

    private static Instance InstanceOf(object? sender) => ((InstanceItem)((Control)sender!).Tag!).Instance;

    private void OnPlay(object? sender, RoutedEventArgs e) => PlayerWindow.Open(_sdk, InstanceOf(sender), new StartOptions());

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

        Item("Cold boot", "IconColdBoot", () => PlayerWindow.Open(_sdk, instance, new StartOptions(ColdBoot: true)), !running);
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
            if (!await Dialogs.ConfirmAsync(this, "Delete instance", $"Delete {instance.DisplayName} and everything in it?", "Delete")) return;
            _store.Delete(instance);
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

    private async void OnEmptyAction(object? sender, RoutedEventArgs e)
    {
        if (!_sdk.IsInstalled)
        {
            var setup = SdkLocation.FindUpwards(OS.SetupScript);
            if (setup is null) return;
            if (!Terminal.RunScript(setup))
                await Dialogs.ShowAsync(this, "Run setup", "Resit could not open a terminal. Run this in one:", $"bash \"{setup}\"");
            return;
        }
        OnNewInstance(sender, e);
    }

    private async void OnEnableHypervisor(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsWindows()) await EnableWindowsHypervisorAsync();
        else await Dialogs.ShowAsync(this, "Hardware acceleration",
            "The emulator needs KVM. Turn on virtualization (Intel VT-x or AMD SVM) in your BIOS, then give your " +
            "user access to KVM with the command below. Log out and back in afterwards.",
            "sudo usermod -aG kvm $USER");
        await CheckAccelerationAsync();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task EnableWindowsHypervisorAsync()
    {
        // Without Hyper-V running, Google's hypervisor driver (AEHD) needs no reboot and leaves
        // other emulators alone. With Hyper-V already on, only WHPX can work.
        var aehd = Path.Combine(_sdk.SdkRoot, "extras", "google", "Android_Emulator_Hypervisor_Driver", "silent_install.bat");
        bool hyperV = HostInfo.IsHypervisorRunning();
        try
        {
            if (!hyperV && File.Exists(aehd))
            {
                if (!await Dialogs.ConfirmAsync(this, "Hardware acceleration",
                        "Install the Android Emulator Hypervisor Driver? Windows will ask for admin rights. No reboot is needed, and other emulators keep working.",
                        "Install")) return;
                using var p = Process.Start(new ProcessStartInfo("cmd.exe")
                {
                    ArgumentList = { "/c", aehd },
                    WorkingDirectory = Path.GetDirectoryName(aehd)!,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (p is not null) await p.WaitForExitAsync();
            }
            else
            {
                if (!await Dialogs.ConfirmAsync(this, "Hardware acceleration",
                        "Turn on the Windows Hypervisor Platform? Windows will ask for admin rights, and you need to restart afterwards.",
                        "Turn on")) return;
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    ArgumentList =
                    {
                        "-NoProfile", "-Command",
                        "Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All -NoRestart; Read-Host 'Done. Restart Windows to finish. Press Enter to close'",
                    },
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
        }
        catch (Win32Exception)
        {
            // The user declined the admin prompt.
        }
    }
}
