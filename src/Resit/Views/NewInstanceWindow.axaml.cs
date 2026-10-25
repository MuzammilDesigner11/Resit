using Avalonia.Controls;
using Avalonia.Interactivity;
using Resit.Core;

namespace Resit.Views;

public partial class NewInstanceWindow : Window
{
    private readonly InstanceStore _store;
    private CancellationTokenSource? _download;
    private int _listVersion;
    private bool _loaded;

    // For the XAML previewer.
    public NewInstanceWindow() : this(new InstanceStore(SdkLocation.Detect())) { }

    public NewInstanceWindow(InstanceStore store)
    {
        InitializeComponent();
        _store = store;

        int n = 1;
        while (_store.Exists(n == 1 ? "Resit" : $"Resit-{n}")) n++;
        NameBox.Text = n == 1 ? "Resit" : $"Resit-{n}";

        PerfBox.ItemsSource = PerformancePreset.For(HostInfo.Current);
        PerfBox.SelectedIndex = PerformancePreset.DefaultIndex;

        DisplayBox.ItemsSource = DisplayPreset.All;
        DisplayBox.SelectedIndex = 0;

        Opened += async (_, _) =>
        {
            NameBox.SelectAll();
            NameBox.Focus();
            _loaded = true;
            await LoadImagesAsync();
        };
        Closing += (_, _) => _download?.Cancel();
    }

    private Edition SelectedEdition => MinimalOption.IsChecked == true ? Edition.Minimal : Edition.GooglePlay;

    private async void OnEditionChanged(object? sender, RoutedEventArgs e)
    {
        if (_loaded) await LoadImagesAsync();
    }

    private async Task LoadImagesAsync()
    {
        int version = ++_listVersion;
        ImageBox.ItemsSource = null;
        ImageBox.IsEnabled = false;
        RetryImages.IsVisible = false;
        ImageHint.Text = "Checking which Android versions are available…";
        var images = await ImageCatalog.ListAsync(_store.Sdk, SelectedEdition);
        if (version != _listVersion) return;   // the edition changed meanwhile

        ImageBox.ItemsSource = images;
        ImageBox.IsEnabled = images.Count > 0;
        // Prefer an installed image (no download), otherwise the newest.
        ImageBox.SelectedItem = images.FirstOrDefault(i => i.Installed) ?? images.FirstOrDefault();
        if (images.Count == 0)
        {
            ImageHint.Text = "Could not get the list of Android versions. Check your internet connection.";
            RetryImages.IsVisible = true;
        }
    }

    private async void OnRetryImages(object? sender, RoutedEventArgs e) => await LoadImagesAsync();

    private void OnImageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ImageBox.SelectedItem is not AvailableImage image)
        {
            CreateButton.Content = "Create";
            return;
        }
        CreateButton.Content = image.Installed ? "Create" : "Download and create";
        ImageHint.Text = image.Installed
            ? "Installed. The instance is ready right away."
            : $"Resit downloads it from Google first ({image.SizeBytes / (1024 * 1024)} MB), under the Android SDK License Agreement.";
    }

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (!InstanceStore.IsValidName(name)) { ShowError("Use 1–64 letters, numbers, dots, dashes or underscores."); return; }
        if (_store.Exists(name)) { ShowError($"An instance named '{name}' already exists."); return; }
        if (ImageBox.SelectedItem is not AvailableImage choice) { ShowError("Choose an Android version."); return; }
        ErrorText.IsVisible = false;

        if (!SdkSetup.IsComplete(_store.Sdk) || !choice.Installed)
        {
            SetBusy(true);
            ProgressPanel.IsVisible = true;
            _download = new CancellationTokenSource();
            try
            {
                if (!SdkSetup.IsComplete(_store.Sdk)) await WaitForEmulatorSetupAsync(_download.Token);
                if (!choice.Installed) await DownloadImageAsync(choice, _download.Token);
            }
            catch (OperationCanceledException)
            {
                SetBusy(false);
                ProgressPanel.IsVisible = false;
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                SetBusy(false);
                ProgressPanel.IsVisible = false;
                ShowError(ex.Message);
                return;
            }
            finally
            {
                _download?.Dispose();
                _download = null;
            }
        }

        var image = SystemImage.FindInstalled(_store.Sdk.SdkRoot)
            .FirstOrDefault(i => i.ApiLevel == choice.ApiLevel && i.Tag == choice.Tag);
        if (image is null)
        {
            SetBusy(false);
            ProgressPanel.IsVisible = false;
            ShowError("The download finished but the system image was not found.");
            return;
        }

        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_store.Sdk.AvdHome))!).AvailableFreeSpace / (1L << 30);
            _store.Create(name, image, SelectedEdition, (PerformancePreset)PerfBox.SelectedItem!, (DisplayPreset)DisplayBox.SelectedItem!,
                dataGB: free < 60 ? 8 : 32);
            Close(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SetBusy(false);
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// The emulator downloads in the background from Resit's first start. Waits for it (starting
    /// it again if it failed) and shows its progress here; Cancel stops waiting, not the download.
    /// </summary>
    private async Task WaitForEmulatorSetupAsync(CancellationToken ct)
    {
        void Show()
        {
            ProgressTitle.Text = "Setting up the Android Emulator";
            if (SdkSetup.Status is { Running: true } s) SetProgress(s.Percent, s.Detail);
        }
        SetProgress(null, "Starting…");
        Show();
        SdkSetup.StatusChanged += Show;
        try
        {
            if (!await SdkSetup.RunAsync(_store.Sdk).WaitAsync(ct))
                throw new InvalidOperationException($"Could not download the Android Emulator. {SdkSetup.Status?.Error}");
        }
        finally
        {
            SdkSetup.StatusChanged -= Show;
        }
    }

    private async Task DownloadImageAsync(AvailableImage choice, CancellationToken ct)
    {
        ProgressTitle.Text = $"Downloading Android {choice.AndroidVersion}";
        SetProgress(null, "Preparing download…");
        var progress = new Progress<InstallProgress>(p =>
        {
            SetProgress(p.Percent == 0 && p.Phase.EndsWith('…') ? null : p.Percent, p.Phase);
            if (p.Phase.StartsWith("Unpacking")) ProgressTitle.Text = $"Unpacking Android {choice.AndroidVersion}";
        });
        await ImageCatalog.InstallAsync(_store.Sdk, choice.Path, progress, ct);
    }

    /// <summary>A null percent shows a moving bar (busy, length unknown).</summary>
    private void SetProgress(double? percent, string detail)
    {
        DownloadBar.IsIndeterminate = percent is null;
        DownloadBar.Value = percent ?? 0;
        ProgressPercent.Text = percent is { } p ? $"{p:0}%" : "";
        ProgressDetail.Text = detail;
    }

    private void SetBusy(bool busy)
    {
        CreateButton.IsEnabled = !busy;
        NameBox.IsEnabled = PlayOption.IsEnabled = MinimalOption.IsEnabled = !busy;
        ImageBox.IsEnabled = PerfBox.IsEnabled = DisplayBox.IsEnabled = !busy;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_download is not null) _download.Cancel();   // stop the download, keep the dialog open
        else Close(false);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
