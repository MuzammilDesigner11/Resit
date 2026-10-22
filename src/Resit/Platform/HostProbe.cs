using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Resit.Core;

namespace Resit.Platform;

/// <summary>Reads the PC's CPU, RAM, GPUs and disks on Windows (WMI).</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHost
{
    public static HostInfo Detect()
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
                    if (!Regex.IsMatch(name, "Virtual|Basic Display|Remote|Mirage|Parsec")) gpus.Add(name);
                }
            }
        }
        catch (ManagementException)
        {
            // WMI can be broken on some machines; HostInfo fills in what .NET knows.
        }
        return new HostInfo(cpu, physical, logical, ramMB, gpus);
    }

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
}

/// <summary>Reads the PC's CPU, RAM, GPUs and disks on Linux (/proc, lspci, lsblk).</summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxHost
{
    public static HostInfo Detect()
    {
        string cpu = "Unknown CPU";
        int logical = 0, ramMB = 0;
        var cores = new HashSet<(string, string)>();
        try
        {
            string physicalId = "0";
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                var (key, value) = Split(line);
                switch (key)
                {
                    case "processor": logical++; break;
                    case "model name" when cpu == "Unknown CPU": cpu = Regex.Replace(value, @"\s+", " "); break;
                    case "physical id": physicalId = value; break;
                    case "core id": cores.Add((physicalId, value)); break;
                }
            }
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var (key, value) = Split(line);
                if (key == "MemTotal" && long.TryParse(value.Split(' ')[0], out var kb)) ramMB = (int)(kb / 1024);
            }
        }
        catch (IOException)
        {
        }
        return new HostInfo(cpu, cores.Count, logical, ramMB, Gpus());
    }

    private static (string Key, string Value) Split(string line)
    {
        int colon = line.IndexOf(':');
        return colon < 0 ? ("", "") : (line[..colon].Trim(), line[(colon + 1)..].Trim());
    }

    /// <summary>Display adapters from lspci, e.g. "NVIDIA GeForce RTX 3080".</summary>
    private static List<string> Gpus()
    {
        var result = new List<string>();
        foreach (var line in Run("lspci", "-mm").Split('\n'))
        {
            var fields = Quoted().Matches(line).Select(m => m.Groups[1].Value).ToList();
            if (fields.Count < 3 || !Regex.IsMatch(fields[0], "VGA|3D|Display")) continue;
            var vendor = Bracketed(fields[1])?.Split('/')[0] ?? fields[1].Split(' ')[0];
            var device = Bracketed(fields[2]) ?? fields[2];
            result.Add($"{vendor} {device}");
        }
        return result;
    }

    private static string? Bracketed(string text)
    {
        var m = Regex.Match(text, @"\[([^\]]+)\]");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True when the path is on a rotating disk (lsblk's ROTA flag, through LVM/LUKS too).</summary>
    public static bool IsOnHardDrive(string path)
    {
        var source = Run("findmnt", "-n", "-o", "SOURCE", "--target", path).Trim();
        int bracket = source.IndexOf('[');   // btrfs subvolumes: /dev/sda2[/@home]
        if (bracket > 0) source = source[..bracket];
        if (!source.StartsWith("/dev/")) return false;
        return Run("lsblk", "-n", "-s", "-o", "ROTA", source).Split('\n').Any(l => l.Trim() == "1");
    }

    private static string Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return text;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex Quoted();
}
