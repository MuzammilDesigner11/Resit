using System.Text.Json;

namespace Resit.Core;

/// <summary>
/// One emulator instance: an AVD folder with the emulator's config.ini plus Resit's own
/// launch options in resit.json.
/// </summary>
public sealed class Instance
{
    public string Name { get; }
    public string Dir { get; }
    public string IniPath { get; }
    public IniFile Config { get; private set; }
    public InstanceOptions Options { get; private set; }

    private Instance(string name, string dir, string iniPath)
    {
        Name = name;
        Dir = dir;
        IniPath = iniPath;
        Config = IniFile.Load(Path.Combine(dir, "config.ini"));
        Options = InstanceOptions.Load(dir);
    }

    public static Instance? Load(string iniPath)
    {
        var ini = IniFile.Load(iniPath);
        var dir = ini.Get("path");
        if (dir is null || !Directory.Exists(dir))
        {
            var rel = ini.Get("path.rel");
            if (rel is null) return null;
            dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(iniPath)!)!, rel);
            if (!Directory.Exists(dir)) return null;
        }
        return new Instance(Path.GetFileNameWithoutExtension(iniPath), dir, iniPath);
    }

    public void Reload()
    {
        Config = IniFile.Load(Config.FilePath);
        Options = InstanceOptions.Load(Dir);
    }

    public void Save()
    {
        Config.Save();
        Options.Save(Dir);
    }

    public string DisplayName
    {
        get => Config.Get("avd.ini.displayname") ?? Name;
        set => Config.Set("avd.ini.displayname", value);
    }

    public SystemImage? Image => SystemImage.FromSysDir(Config.Get("image.sysdir.1"));

    public int Cores
    {
        get => Config.GetInt("hw.cpu.ncore", 2);
        set => Config.Set("hw.cpu.ncore", value);
    }

    public int RamMB
    {
        get => Config.GetSizeMB("hw.ramSize", 2048);
        set => Config.Set("hw.ramSize", value);
    }

    public int HeapMB
    {
        get => Config.GetSizeMB("vm.heapSize", 256);
        set => Config.Set("vm.heapSize", value);
    }

    public int DataGB
    {
        get => Config.GetSizeMB("disk.dataPartition.size", 6144) / 1024;
        set => Config.Set("disk.dataPartition.size", $"{value}G");
    }

    public int Width
    {
        get => Config.GetInt("hw.lcd.width", 1080);
        set => Config.Set("hw.lcd.width", value);
    }

    public int Height
    {
        get => Config.GetInt("hw.lcd.height", 1920);
        set => Config.Set("hw.lcd.height", value);
    }

    public int Density
    {
        get => Config.GetInt("hw.lcd.density", 420);
        set => Config.Set("hw.lcd.density", value);
    }

    public int RefreshRate
    {
        get => Config.GetInt("hw.lcd.vsync", 60);
        set => Config.Set("hw.lcd.vsync", value);
    }

    /// <summary>auto, host, angle_indirect or swiftshader_indirect.</summary>
    public string GpuMode
    {
        get => Config.Get("hw.gpu.mode") ?? "auto";
        set
        {
            Config.Set("hw.gpu.enabled", true);
            Config.Set("hw.gpu.mode", value);
        }
    }

    /// <summary>
    /// The emulator's microphone. Off by default: whenever Android opens it, Windows switches
    /// Bluetooth headphones into call (headset) mode, which cuts out the normal sound.
    /// </summary>
    public bool Microphone
    {
        get => Config.GetBool("hw.audioInput", false);
        set => Config.Set("hw.audioInput", value);
    }

    public bool PlayStore => Config.GetBool("PlayStore.enabled", false);

    public string Summary => $"{Cores} cores · {RamMB / 1024.0:0.#} GB · {Width}×{Height}";

    public string ImageSummary => Image is { } img
        ? $"Android {img.AndroidVersion} · {(Options.Edition == Edition.Minimal ? "Minimal" : img.TagDisplay)}"
        : "Unknown image";
}

/// <summary>Launch-time options that are not part of the emulator's config.ini.</summary>
public sealed class InstanceOptions
{
    private const string FileName = "resit.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Save a snapshot on exit and resume from it (seconds instead of a full boot).</summary>
    public bool QuickBoot { get; set; } = true;
    public bool Audio { get; set; } = true;
    /// <summary>Windows sound device for this instance (endpoint id); null follows the Windows default.</summary>
    public string? OutputDevice { get; set; }
    /// <summary>Windows microphone for this instance (endpoint id); null follows the Windows default.</summary>
    public string? InputDevice { get; set; }
    /// <summary>Run the VM process at High priority.</summary>
    public bool HighPriority { get; set; } = true;
    public bool Vulkan { get; set; } = true;
    /// <summary>Rotate the window when the app on screen switches between portrait and landscape.</summary>
    public bool AutoRotate { get; set; } = true;
    public bool StartFullscreen { get; set; }
    /// <summary>Show key labels over the screen while playing.</summary>
    public bool ShowKeyHints { get; set; } = true;
    /// <summary>Decode video in software instead of on the host GPU (fixes some protected or unusual streams).</summary>
    public bool SoftwareVideo { get; set; }
    /// <summary>Send Android's DNS through an ad-blocking resolver, so ads and trackers fail to load in every app.</summary>
    public bool BlockAds { get; set; } = true;
    public Edition Edition { get; set; } = Edition.GooglePlay;
    /// <summary>For the Minimal edition: whether the extra apps have been removed yet (done after first boot).</summary>
    public bool MinimalSetupDone { get; set; }

    public static InstanceOptions Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<InstanceOptions>(File.ReadAllText(path), Json) ?? new();
        }
        catch (JsonException)
        {
        }
        return new();
    }

    public void Save(string dir) => File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Json));
}
