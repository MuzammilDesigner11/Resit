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
        ImageHint.Text = "Checking which Android versions are available…";
        var images = await ImageCatalog.ListAsync(_store.Sdk, SelectedEdition);
        if (version != _listVersion) return;   // the edition changed meanwhile

        ImageBox.ItemsSource = images;
        ImageBox.IsEnabled = images.Count > 0;
        // Prefer an installed image (no download), otherwise the newest.
        ImageBox.SelectedItem = images.FirstOrDefault(i => i.Installed) ?? images.FirstOrDefault();
        if (images.Count == 0) ImageHint.Text = "No images found. Check your internet connection.";
    }

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
            : $"Resit downloads it from Google first ({image.SizeBytes / (1024 * 1024)} MB).";
    }

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (!InstanceStore.IsValidName(name)) { ShowError("Use 1–64 letters, numbers, dots, dashes or underscores."); return; }
        if (_store.Exists(name)) { ShowError($"An instance named '{name}' already exists."); return; }
        if (ImageBox.SelectedItem is not AvailableImage choice) { ShowError("Choose an Android version."); return; }
        ErrorText.IsVisible = false;

        if (!choice.Installed)
        {
            SetBusy(true);
            ProgressTitle.Text = $"Downloading Android {choice.AndroidVersion}";
            ProgressPanel.IsVisible = true;
            _download = new CancellationTokenSource();
            var progress = new Progress<InstallProgress>(p =>
            {
                DownloadBar.Value = p.Percent;
                DownloadBar.IsIndeterminate = p.Percent == 0 && p.Phase.EndsWith('…');
                ProgressPercent.Text = DownloadBar.IsIndeterminate ? "" : $"{p.Percent:0}%";
                ProgressDetail.Text = p.Phase;
                if (p.Phase.StartsWith("Unpacking")) ProgressTitle.Text = $"Unpacking Android {choice.AndroidVersion}";
            });
            try
            {
                await ImageCatalog.InstallAsync(_store.Sdk, choice.Path, progress, _download.Token);
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
