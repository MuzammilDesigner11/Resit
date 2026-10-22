using System.Text.Json;
using System.Text.RegularExpressions;
using Resit.Platform;

namespace Resit.Core;

/// <summary>Limits the emulator reports about this machine, remembered between runs.</summary>
public static partial class HostLimits
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "host.json");
    private static int? _maxCores = Load();

    public static int? MaxEmulatorCores => _maxCores;

    /// <summary>Picks up "Emulator does not support more than N cores" from an emulator log.</summary>
    public static void LearnFromLog(string logPath)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var m = CoreWarning().Match(new StreamReader(stream).ReadToEnd());
            if (!m.Success) return;
            _maxCores = int.Parse(m.Groups[1].Value);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dictionary<string, int> { ["maxEmulatorCores"] = _maxCores.Value }));
        }
        catch (IOException)
        {
        }
    }

    private static int? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var d = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath));
            return d is not null && d.TryGetValue("maxEmulatorCores", out var v) ? v : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"does not support more than (\d+) cores")]
    private static partial Regex CoreWarning();
}

/// <summary>The host PC's hardware, used to size instances.</summary>
public sealed record HostInfo(string CpuName, int PhysicalCores, int LogicalCores, int RamMB, IReadOnlyList<string> Gpus)
{
    private static readonly Lazy<HostInfo> _current = new(Detect);

    public static HostInfo Current => _current.Value;

    /// <summary>
    /// One vCPU per physical core: beyond that, SMT siblings add contention rather than speed,
    /// and the host keeps its hyperthreads for the emulator's render and I/O threads. Also capped
    /// at what the emulator accepts with this hypervisor, once Resit has seen that limit.
    /// </summary>
    public int MaxCores => Math.Clamp(Math.Min(PhysicalCores, CoreLimit), 2, 16);

    /// <summary>Most vCPUs the emulator accepts on this machine (it silently clamps above this).</summary>
    public int CoreLimit => Math.Min(LogicalCores, HostLimits.MaxEmulatorCores ?? 16);

    /// <summary>
    /// Half of host RAM, rounded down to a whole GB, between 2 and 8 GB. Android rarely uses
    /// more, and Quick Boot snapshots hold all of it: every extra GB is read on each resume.
    /// </summary>
    public int MaxRamMB => Math.Clamp(RamMB / 2 / 1024 * 1024, 2048, 8192);

    /// <summary>The sweet spot for most games: fast snapshots, plenty for Android.</summary>
    public int DefaultRamMB => Math.Min(4096, MaxRamMB);

    /// <summary>True when a path is on a spinning hard drive (Android boots several times slower there).</summary>
    public static bool IsOnHardDrive(string path) =>
        OperatingSystem.IsWindows() ? WindowsHost.IsOnHardDrive(path) :
        OperatingSystem.IsLinux() && LinuxHost.IsOnHardDrive(path);

    /// <summary>True when Windows itself runs under Hyper-V (then only WHPX can accelerate the emulator).</summary>
    public static bool IsHypervisorRunning() => OperatingSystem.IsWindows() && WindowsHost.IsHypervisorRunning();

    private static HostInfo Detect()
    {
        var info = OperatingSystem.IsWindows() ? WindowsHost.Detect()
            : OperatingSystem.IsLinux() ? LinuxHost.Detect()
            : new HostInfo("Unknown CPU", 0, 0, 0, []);
        // Fill in what .NET knows where the OS query came back empty.
        int logical = info.LogicalCores > 0 ? info.LogicalCores : Environment.ProcessorCount;
        int physical = info.PhysicalCores > 0 ? info.PhysicalCores : Math.Max(1, logical / 2);
        int ram = info.RamMB > 0 ? info.RamMB : (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        return info with { LogicalCores = logical, PhysicalCores = physical, RamMB = ram };
    }
}
