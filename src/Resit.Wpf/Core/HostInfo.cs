using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    public static bool IsOnHardDrive(string path)
    {
        try
        {
            var letter = Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\', ':');
            if (string.IsNullOrEmpty(letter)) return false;
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var parts = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter[0]}'"));
            var disk = parts.Get().Cast<ManagementBaseObject>().Select(o => Convert.ToUInt32(o["DiskNumber"])).FirstOrDefault(uint.MaxValue);
            if (disk == uint.MaxValue) return false;
            using var disks = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{disk}'"));
            // MediaType 3 = HDD, 4 = SSD, 0 = unknown.
            return disks.Get().Cast<ManagementBaseObject>().Any(o => Convert.ToUInt16(o["MediaType"]) == 3);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when Windows itself runs under Hyper-V (then only WHPX can accelerate the emulator).</summary>
    public static bool IsHypervisorRunning()
    {
        try
        {
            using var q = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
            return q.Get().Cast<ManagementBaseObject>().Any(o => o["HypervisorPresent"] is true);
        }
        catch (ManagementException)
        {
            return false;
        }
    }

    private static HostInfo Detect()
    {
        string cpu = "Unknown CPU";
        int physical = 0, logical = 0, ramMB = 0;
        var gpus = new List<string>();
        try
        {
            using (var q = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                foreach (var o in q.Get())
                {
                    cpu = ((string)o["Name"]).Trim();
                    physical += Convert.ToInt32(o["NumberOfCores"]);
                    logical += Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                }
            }
            using (var q = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                foreach (var o in q.Get()) ramMB = (int)(Convert.ToUInt64(o["TotalPhysicalMemory"]) / (1024 * 1024));
            }
            using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            {
                foreach (var o in q.Get())
                {
                    var name = (string)o["Name"];
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "Virtual|Basic Display|Remote|Mirage|Parsec"))
                        gpus.Add(name);
                }
            }
        }
        catch (ManagementException)
        {
            // WMI can be broken on some machines; fall back to what .NET knows.
        }
        if (logical == 0) logical = Environment.ProcessorCount;
        if (physical == 0) physical = Math.Max(1, logical / 2);
        if (ramMB == 0) ramMB = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        return new HostInfo(cpu, physical, logical, ramMB, gpus);
    }
}
