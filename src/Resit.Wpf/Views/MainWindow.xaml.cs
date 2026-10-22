using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
            OnPropertyChanged(nameof(PlayGlyph));
        }
    }

    public string Status => IsRunning ? "Running" : "Stopped";
    public Brush StatusBackground => IsRunning ? new SolidColorBrush(Color.FromArgb(0x26, 0x3D, 0xD6, 0x8C)) : new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    public Brush StatusForeground => IsRunning ? (Brush)Application.Current.Resources["Success"] : (Brush)Application.Current.Resources["TextSecondary"];
    public string PlayLabel => IsRunning ? "Open" : "Play";
    public string PlayGlyph => IsRunning ? "" : "";

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
        Loaded += async (_, _) =>
        {
            Refresh();
            await CheckAccelerationAsync();
            if (await Task.Run(() => HostInfo.IsOnHardDrive(_sdk.AvdHome))) DiskWarning.Visibility = Visibility.Visible;
        };
        _statusTimer.Tick += (_, _) => UpdateRunningState();
        _statusTimer.Start();
        PlayerWindow.SessionsChanged += (_, _) => UpdateRunningState();
    }

    private void Refresh()
    {
        if (!_sdk.IsInstalled)
        {
            InstanceList.ItemsSource = null;
            var reason = _sdk.MissingReason();
            Log.Write($"SDK not found at {_sdk.SdkRoot}: {reason}");
            ShowEmpty("", "Android SDK not found",
                $"Resit needs the Android Emulator. It looked in {_sdk.SdkRoot}. {reason} Run setup to download it (about 3 GB).",
                SdkLocation.FindUpwards("setup.ps1") is null ? null : "Run setup");
            NewButton.IsEnabled = false;
            return;
        }

        NewButton.IsEnabled = true;
        _items = _store.List().Select(i => new InstanceItem(i)).ToList();
        InstanceList.ItemsSource = _items;
        UpdateRunningState();
        if (_items.Count == 0)
            ShowEmpty("", "No instances yet", "Create an instance to start playing. Resit sizes it to your PC automatically.", "Create instance");
        else
            EmptyState.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string glyph, string title, string text, string? action)
    {
        EmptyIcon.Text = glyph;
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyAction.Content = action;
        EmptyAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;
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
            AccelDot.Fill = (Brush)FindResource(ok ? "Success" : "Warning");
            AccelText.Text = ok ? $"Hardware acceleration ready · {line}" : "Hardware acceleration is off. The emulator will be very slow.";
            AccelFix.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AccelText.Text = "Could not check hardware acceleration";
        }
    }

    private static Instance InstanceOf(object sender) => ((InstanceItem)((FrameworkElement)sender).Tag).Instance;

    private void OnPlay(object sender, RoutedEventArgs e) => PlayerWindow.Open(_sdk, InstanceOf(sender), new StartOptions());

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var instance = InstanceOf(sender);
        var dialog = new SettingsWindow(instance, PlayerWindow.IsOpen(instance.Name)) { Owner = this };
        if (dialog.ShowDialog() == true) Refresh();
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        var instance = InstanceOf(sender);
        bool running = PlayerWindow.IsOpen(instance.Name) || RunningEmulator.List().Any(r => r.AvdName == instance.Name);
        // Explicit text font: otherwise the menu inherits the icon font from the ⋯ button and
        // every label renders as missing-glyph boxes.
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, FontFamily = SystemFonts.MessageFontFamily, FontSize = 14 };

        MenuItem Item(string header, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = enabled,
                Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont") },
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        Item("Cold boot", "", () => PlayerWindow.Open(_sdk, instance, new StartOptions(ColdBoot: true)), !running);
        Item("Wipe data…", "", () =>
        {
            if (MessageBox.Show(this, $"Erase all apps and data in {instance.DisplayName}? This cannot be undone.", "Wipe data",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            InstanceStore.WipeData(instance);
        }, !running);
        Item("Open folder", "", () => Process.Start(new ProcessStartInfo(instance.Dir) { UseShellExecute = true }));
        Item("Open log", "", () =>
        {
            var log = Path.Combine(instance.Dir, "resit-emulator.log");
            if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        });
        menu.Items.Add(new Separator());
        Item("Delete…", "", () =>
        {
            if (MessageBox.Show(this, $"Delete {instance.DisplayName} and everything in it?", "Delete instance",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _store.Delete(instance);
            Refresh();
        }, !running);
        menu.IsOpen = true;
    }

    private void OnNewInstance(object sender, RoutedEventArgs e)
    {
        var dialog = new NewInstanceWindow(_store) { Owner = this };
        if (dialog.ShowDialog() == true) Refresh();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnEmptyAction(object sender, RoutedEventArgs e)
    {
        if (!_sdk.IsInstalled)
        {
            var setup = SdkLocation.FindUpwards("setup.ps1");
            if (setup is null) return;
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", setup },
                WorkingDirectory = Path.GetDirectoryName(setup)!,
                UseShellExecute = true,
            });
            return;
        }
        OnNewInstance(sender, e);
    }

    private async void OnEnableHypervisor(object sender, RoutedEventArgs e)
    {
        // Without Hyper-V running, Google's hypervisor driver (AEHD) needs no reboot and leaves
        // other emulators alone. With Hyper-V already on, only WHPX can work.
        var aehd = Path.Combine(_sdk.SdkRoot, "extras", "google", "Android_Emulator_Hypervisor_Driver", "silent_install.bat");
        bool hyperV = HostInfo.IsHypervisorRunning();
        try
        {
            if (!hyperV && File.Exists(aehd))
            {
                if (MessageBox.Show(this,
                        "Install the Android Emulator Hypervisor Driver?\n\nWindows will ask for admin rights. No reboot is needed, and other emulators keep working.",
                        "Hardware acceleration", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
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
                if (MessageBox.Show(this,
                        "Turn on the Windows Hypervisor Platform?\n\nWindows will ask for admin rights, and you need to restart afterwards.",
                        "Hardware acceleration", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
                using var p = Process.Start(new ProcessStartInfo("powershell.exe")
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
        catch (System.ComponentModel.Win32Exception)
        {
            return;   // the user declined the UAC prompt
        }
        await CheckAccelerationAsync();
    }
}
