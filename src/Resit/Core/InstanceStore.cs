using System.Text;
using System.Text.RegularExpressions;

namespace Resit.Core;

public sealed record DisplayPreset(string Name, int Width, int Height, int Density)
{
    public override string ToString() => Name;

    // Widescreen first: like other gaming emulators, instances start in landscape and fill a
    // monitor in fullscreen. Portrait apps still work: the window turns to match them.
    public static readonly DisplayPreset[] All =
    [
        new("Widescreen · 1920 × 1080", 1920, 1080, 280),
        new("Widescreen · 1600 × 900", 1600, 900, 240),
        new("Widescreen · 1280 × 720", 1280, 720, 200),
        new("Widescreen · 2560 × 1440", 2560, 1440, 360),
        new("Phone · 1080 × 2400 (Pixel 8)", 1080, 2400, 420),
        new("Phone · 1080 × 1920", 1080, 1920, 420),
        new("Phone · 720 × 1280", 720, 1280, 320),
        new("Phone · 1440 × 3120", 1440, 3120, 560),
        new("Tablet · 1600 × 2560", 1600, 2560, 320),
    ];
}

public sealed record PerformancePreset(string Name, int Cores, int RamMB)
{
    public override string ToString() => Name;

    /// <summary>Index of the preset new instances start with.</summary>
    public const int DefaultIndex = 1;

    // Every preset but Light uses all the cores the emulator accepts: cores cost nothing while
    // idle. RAM is what costs, because Quick Boot saves and reloads all of it.
    public static PerformancePreset[] For(HostInfo host) =>
    [
        new("Light · 2 cores, 2 GB", 2, 2048),
        new($"Fast · {host.MaxCores} cores, {host.DefaultRamMB / 1024} GB (recommended)", host.MaxCores, host.DefaultRamMB),
        new($"Heavy games · {host.MaxCores} cores, {Math.Min(6144, host.MaxRamMB) / 1024} GB", host.MaxCores, Math.Min(6144, host.MaxRamMB)),
        new($"Maximum · {host.MaxCores} cores, {host.MaxRamMB / 1024} GB", host.MaxCores, host.MaxRamMB),
    ];
}

/// <summary>Lists, creates and deletes instances in the AVD folder.</summary>
public sealed partial class InstanceStore(SdkLocation sdk)
{
    public SdkLocation Sdk { get; } = sdk;

    public List<Instance> List()
    {
        if (!Directory.Exists(Sdk.AvdHome)) return [];
        return Directory.EnumerateFiles(Sdk.AvdHome, "*.ini")
            .Select(Instance.Load)
            .OfType<Instance>()
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    public bool Exists(string name) =>
        File.Exists(Path.Combine(Sdk.AvdHome, name + ".ini")) || Directory.Exists(Path.Combine(Sdk.AvdHome, name + ".avd"));

    public Instance Create(string name, SystemImage image, Edition edition, PerformancePreset perf, DisplayPreset display, int dataGB)
    {
        if (!IsValidName(name)) throw new ArgumentException("Use letters, numbers, dots, dashes or underscores.", nameof(name));
        if (Exists(name)) throw new InvalidOperationException($"An instance named '{name}' already exists.");

        var dir = Path.Combine(Sdk.AvdHome, name + ".avd");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(Sdk.AvdHome, name + ".ini"),
            ["avd.ini.encoding=UTF-8", $"path={dir}", $"target={image.ApiId}"], new UTF8Encoding(false));

        var c = IniFile.Load(Path.Combine(dir, "config.ini"));
        c.Set("AvdId", name);
        c.Set("avd.ini.displayname", name);
        c.Set("avd.ini.encoding", "UTF-8");
        c.Set("target", image.ApiId);
        c.Set("image.sysdir.1", image.RelativePath);
        c.Set("tag.id", image.Tag);
        c.Set("tag.display", image.TagDisplay);
        c.Set("abi.type", image.Abi);
        c.Set("hw.cpu.arch", image.Abi);
        c.Set("PlayStore.enabled", image.PlayStore ? "true" : "false");

        c.Set("hw.cpu.ncore", perf.Cores);
        c.Set("hw.ramSize", perf.RamMB);
        c.Set("vm.heapSize", 512);   // per-app heap; the ceiling flagship phones ship with
        c.Set("disk.dataPartition.size", $"{dataGB}G");
        c.Set("hw.sdCard", false);
        c.Set("hw.gpu.enabled", true);
        c.Set("hw.gpu.mode", "host");

        // No hw.device.name: naming a phone (e.g. pixel_8) makes the emulator imitate its camera
        // cutout and rounded corners, which only get in the way on a desktop screen.
        c.Set("hw.lcd.width", display.Width);
        c.Set("hw.lcd.height", display.Height);
        c.Set("hw.lcd.density", display.Density);
        c.Set("hw.lcd.vsync", 60);
        c.Set("hw.initialOrientation", display.Width > display.Height ? "landscape" : "portrait");
        c.Set("showDeviceFrame", false);
        c.Set("hw.keyboard", true);
        c.Set("hw.mainKeys", false);
        c.Set("hw.dPad", false);
        c.Set("hw.trackBall", false);
        c.Set("hw.accelerometer", true);
        c.Set("hw.gyroscope", true);
        c.Set("hw.sensors.orientation", true);
        c.Set("hw.sensors.proximity", true);
        c.Set("hw.gps", true);
        c.Set("hw.battery", true);
        c.Set("hw.audioInput", false);   // mic off: opening it flips Bluetooth headphones into call mode
        c.Set("hw.audioOutput", true);
        c.Set("hw.camera.back", "emulated");
        c.Set("hw.camera.front", "emulated");
        c.Set("runtime.network.latency", "none");
        c.Set("runtime.network.speed", "full");
        c.Set("fastboot.forceColdBoot", false);
        c.Set("fastboot.forceFastBoot", true);
        c.Save();

        new InstanceOptions { Edition = edition }.Save(dir);
        return Instance.Load(Path.Combine(Sdk.AvdHome, name + ".ini"))!;
    }

    public void Delete(Instance instance)
    {
        if (Directory.Exists(instance.Dir))
        {
            // The emulator marks some files read-only (e.g. data\misc\pstore\pstore.bin), which
            // Directory.Delete refuses to remove.
            foreach (var file in Directory.EnumerateFiles(instance.Dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(instance.Dir, recursive: true);
        }
        if (File.Exists(instance.IniPath)) File.Delete(instance.IniPath);
    }

    /// <summary>Factory reset: removes user data and snapshots; the next boot starts fresh.</summary>
    public static void WipeData(Instance instance)
    {
        foreach (var name in new[] { "userdata-qemu.img", "userdata-qemu.img.qcow2", "cache.img", "cache.img.qcow2", "encryptionkey.img", "encryptionkey.img.qcow2" })
        {
            var path = Path.Combine(instance.Dir, name);
            if (File.Exists(path)) File.Delete(path);
        }
        var snapshots = Path.Combine(instance.Dir, "snapshots");
        if (Directory.Exists(snapshots)) Directory.Delete(snapshots, recursive: true);
        // A wiped Minimal instance gets its extra apps back, so trim it again on next start.
        instance.Options.MinimalSetupDone = false;
        instance.Options.Save(instance.Dir);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidName();
}
