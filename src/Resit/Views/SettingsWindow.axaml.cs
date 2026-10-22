using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Resit.Core;
using Resit.Platform;

namespace Resit.Views;

public partial class SettingsWindow : Window
{
    private sealed record Choice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    // ANGLE (DirectX) is a Windows-only renderer.
    private static readonly Choice[] Renderers = OS.IsWindows
        ?
        [
            new("GPU · OpenGL (fastest)", "host"),
            new("Auto (let the emulator choose)", "auto"),
            new("GPU · DirectX via ANGLE (compatibility)", "angle_indirect"),
            new("Software (slow, for broken drivers)", "swiftshader_indirect"),
        ]
        :
        [
            new("GPU · OpenGL (fastest)", "host"),
            new("Auto (let the emulator choose)", "auto"),
            new("Software (slow, for broken drivers)", "swiftshader_indirect"),
        ];

    private static readonly Choice[] VideoModes =
    [
        new("Hardware · decoded on your graphics card (fastest)", "hardware"),
        new("Software · decoded in Android (compatibility)", "software"),
    ];

    private readonly Instance _instance;
    private readonly HostInfo _host = HostInfo.Current;
    private bool _loading = true;

    // For the XAML previewer.
    public SettingsWindow() : this(new InstanceStore(SdkLocation.Detect()).List().First(), false) { }

    public SettingsWindow(Instance instance, bool running)
    {
        InitializeComponent();
        _instance = instance;
        instance.Reload();
        TitleText.Text = $"{instance.DisplayName} settings";
        SubtitleText.Text = $"{instance.ImageSummary} · {instance.Name}";
        FooterText.Text = running ? "This instance is running. Changes apply the next time it starts." : "";

        // Performance
        var presets = PerformancePreset.For(_host).ToList<object>();
        presets.Add("Custom");
        PerfPresetBox.ItemsSource = presets;
        CoresSlider.Maximum = _host.CoreLimit;
        CoresSlider.Value = Math.Min(instance.Cores, _host.CoreLimit);
        CoresHint.Text = $"Your CPU has {_host.PhysicalCores} cores ({_host.LogicalCores} threads). " +
                         (_host.CoreLimit < _host.LogicalCores
                             ? $"The emulator accepts up to {_host.CoreLimit} with your hypervisor."
                             : $"{_host.MaxCores} is the sweet spot; more threads than cores rarely helps.");
        RamSlider.Maximum = Math.Max(2048, Math.Min(32768, _host.RamMB * 3 / 4 / 512 * 512));
        RamSlider.Value = Math.Clamp(instance.RamMB, 2048, RamSlider.Maximum);
        RamHint.Text = $"Your PC has {_host.RamMB / 1024.0:0} GB. {_host.DefaultRamMB / 1024} GB suits most games. " +
                       "More makes Quick Boot slower: the snapshot holds all of it, so it is written on close and read on start.";
        HighPriorityBox.IsChecked = instance.Options.HighPriority;
        PriorityHint.Text = OS.IsWindows
            ? "Windows schedules the emulator ahead of background apps."
            : "Linux schedules the emulator ahead of background apps. Raising priority needs permission (CAP_SYS_NICE); without it the setting has no effect.";
        SelectMatchingPerfPreset();

        // Display
        var resolutions = DisplayPreset.All.ToList<object>();
        resolutions.Add("Custom");
        ResolutionBox.ItemsSource = resolutions;
        WidthBox.Text = instance.Width.ToString();
        HeightBox.Text = instance.Height.ToString();
        DpiBox.Text = instance.Density.ToString();
        ResolutionBox.SelectedItem = DisplayPreset.All.FirstOrDefault(p =>
            p.Width == instance.Width && p.Height == instance.Height && p.Density == instance.Density) ?? (object)"Custom";
        var rates = new[] { 60, 90, 120 }.Select(hz => new Choice($"{hz} Hz", hz.ToString())).ToList();
        RefreshBox.ItemsSource = rates;
        RefreshBox.SelectedItem = rates.FirstOrDefault(c => c.Value == instance.RefreshRate.ToString()) ?? rates[0];
        AutoRotateBox.IsChecked = instance.Options.AutoRotate;
        FullscreenBox.IsChecked = instance.Options.StartFullscreen;

        // Graphics
        RendererBox.ItemsSource = Renderers;
        RendererBox.SelectedItem = Renderers.FirstOrDefault(r => r.Value == instance.GpuMode) ?? Renderers[0];
        GpuHint.Text = _host.Gpus.Count > 0 ? $"Graphics card: {string.Join(", ", _host.Gpus)}" : "";
        VulkanBox.IsChecked = instance.Options.Vulkan;
        VideoBox.ItemsSource = VideoModes;
        VideoBox.SelectedItem = instance.Options.SoftwareVideo ? VideoModes[1] : VideoModes[0];

        // Device
        DisplayNameBox.Text = instance.DisplayName;
        StorageSlider.Value = Math.Clamp(instance.DataGB, 4, 256);
        StorageSlider.Minimum = Math.Max(4, instance.DataGB);
        BlockAdsBox.IsChecked = instance.Options.BlockAds;
        ImageText.Text = instance.Image?.DisplayName ?? "Unknown system image";
        PlayStoreText.Text = instance.PlayStore
            ? "Includes Google Play. Root access is not available on Play Store images."
            : "No Google Play. Root access is available through adb root.";

        // Audio
        AudioBox.IsChecked = instance.Options.Audio;
        MicrophoneBox.IsChecked = instance.Microphone;
        FillDevices(OutputBox, Flow.Output, instance.Options.OutputDevice);
        FillDevices(InputBox, Flow.Input, instance.Options.InputDevice);

        // Advanced
        QuickBootBox.IsChecked = instance.Options.QuickBoot;
        KeyHintsBox.IsChecked = instance.Options.ShowKeyHints;
        if (!NativeWindows.Current.SupportsClickThroughOverlay)
        {
            KeyHintsBox.IsEnabled = false;
            KeyHintsHint.Text = "Not available on Linux yet: the labels show while you edit the controls.";
        }

        UpdateLabels();
        _loading = false;
    }

    /// <summary>"System default" (empty id) first, then the active devices. A saved device that
    /// is unplugged right now still shows, so saving doesn't silently forget it.</summary>
    private static void FillDevices(ComboBox box, Flow flow, string? selectedId)
    {
        var items = new List<AudioDevice> { new("", OS.DefaultDeviceName) };
        items.AddRange(AudioDevices.List(flow));
        if (!string.IsNullOrEmpty(selectedId) && items.All(d => d.Id != selectedId))
            items.Add(new AudioDevice(selectedId, "Saved device (not connected)"));
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(d => d.Id == (selectedId ?? "")) ?? items[0];
    }

    private void SelectMatchingPerfPreset()
    {
        var match = PerfPresetBox.Items.OfType<PerformancePreset>()
            .FirstOrDefault(p => p.Cores == (int)CoresSlider.Value && p.RamMB == (int)RamSlider.Value);
        PerfPresetBox.SelectedItem = match ?? (object)"Custom";
    }

    private void UpdateLabels()
    {
        CoresValue.Text = $"{(int)CoresSlider.Value}";
        RamValue.Text = $"{RamSlider.Value / 1024:0.#} GB";
        StorageValue.Text = $"{(int)StorageSlider.Value} GB";
    }

    private void OnPerfPreset(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PerfPresetBox.SelectedItem is not PerformancePreset p) return;
        _loading = true;
        CoresSlider.Value = p.Cores;
        RamSlider.Value = p.RamMB;
        _loading = false;
        UpdateLabels();
    }

    private void OnPerfSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (CoresValue is null) return;   // still loading the XAML
        UpdateLabels();
        if (_loading) return;
        _loading = true;
        SelectMatchingPerfPreset();
        _loading = false;
    }

    private void OnStorageChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (StorageValue is not null) UpdateLabels();
    }

    private void OnResolutionPreset(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ResolutionBox.SelectedItem is not DisplayPreset p) return;
        WidthBox.Text = p.Width.ToString();
        HeightBox.Text = p.Height.ToString();
        DpiBox.Text = p.Density.ToString();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out var w) || w < 320 || w > 4096 ||
            !int.TryParse(HeightBox.Text, out var h) || h < 320 || h > 4096 ||
            !int.TryParse(DpiBox.Text, out var dpi) || dpi < 120 || dpi > 640)
        {
            await Dialogs.ShowAsync(this, "Display", "Width and height must be 320–4096 pixels and DPI 120–640.");
            return;
        }

        var i = _instance;
        i.Cores = (int)CoresSlider.Value;
        i.RamMB = (int)RamSlider.Value;
        i.Options.HighPriority = HighPriorityBox.IsChecked == true;

        i.Width = w;
        i.Height = h;
        i.Density = dpi;
        i.Config.Set("hw.initialOrientation", w > h ? "landscape" : "portrait");
        i.RefreshRate = int.Parse(((Choice)RefreshBox.SelectedItem!).Value);
        i.Options.AutoRotate = AutoRotateBox.IsChecked == true;
        i.Options.StartFullscreen = FullscreenBox.IsChecked == true;

        i.GpuMode = ((Choice)RendererBox.SelectedItem!).Value;
        i.Options.Vulkan = VulkanBox.IsChecked == true;
        i.Options.SoftwareVideo = ((Choice)VideoBox.SelectedItem!).Value == "software";

        if (!string.IsNullOrWhiteSpace(DisplayNameBox.Text)) i.DisplayName = DisplayNameBox.Text.Trim();
        i.DataGB = (int)StorageSlider.Value;
        i.Options.BlockAds = BlockAdsBox.IsChecked == true;

        i.Options.QuickBoot = QuickBootBox.IsChecked == true;
        i.Options.Audio = AudioBox.IsChecked == true;
        i.Microphone = MicrophoneBox.IsChecked == true;
        i.Options.OutputDevice = (OutputBox.SelectedItem as AudioDevice)?.Id is { Length: > 0 } output ? output : null;
        i.Options.InputDevice = (InputBox.SelectedItem as AudioDevice)?.Id is { Length: > 0 } input ? input : null;
        i.Options.ShowKeyHints = KeyHintsBox.IsChecked == true;

        i.Save();
        Close(true);
    }
}
