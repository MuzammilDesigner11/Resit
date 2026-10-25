using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Resit.Platform;

namespace Resit.Core;

public enum EnableResult { Ready, RestartNeeded, Declined, Failed }

/// <summary>
/// Hardware acceleration, which the emulator needs to run x86_64 Android at all: checks it and,
/// on Windows, turns it on.
/// </summary>
public static class Acceleration
{
    // Remembers which boot the Windows Hypervisor Platform was turned on in, to tell "restart
    // pending" from "restarted, still not working".
    private static readonly string RestartMarker = Path.Combine(AppPaths.DataDir, "hypervisor-restart");

    /// <summary>The emulator's own verdict (-accel-check) and the line that explains it.</summary>
    public static async Task<(bool Ok, string Detail)> CheckAsync(SdkLocation sdk)
    {
        var (_, output) = await ProcessRunner.RunAsync(sdk.Emulator, ["-accel-check"], TimeSpan.FromSeconds(20));
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("usable") || l.Contains("installed")) ?? "";
        bool ok = output.Contains("is installed and usable");
        if (ok) File.Delete(RestartMarker);
        return (ok, line);
    }

    /// <summary>Resit turned on the Windows Hypervisor Platform, and Windows has not restarted since.</summary>
    public static bool RestartPending => MarkedBoot() is { } boot && (boot - BootTime).Duration() < TimeSpan.FromMinutes(5);

    /// <summary>Resit turned on the Windows Hypervisor Platform and Windows restarted since, yet it still does not work.</summary>
    public static bool RestartedWithoutEffect => MarkedBoot() is not null && !RestartPending;

    private static DateTime BootTime => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static DateTime? MarkedBoot()
    {
        try
        {
            return File.Exists(RestartMarker) && long.TryParse(File.ReadAllText(RestartMarker), out var ticks)
                ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns hardware acceleration on; Windows asks for admin rights. Without Hyper-V running,
    /// installs Google's hypervisor driver (AEHD): no restart, and other emulators keep working.
    /// When that is not possible (Hyper-V is on, or the driver does not work on this PC), turns
    /// on the Windows Hypervisor Platform, which works after a restart.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<(EnableResult Result, string? Error)> EnableOnWindowsAsync(SdkLocation sdk, IProgress<string> progress)
    {
        bool hyperV = WindowsHost.IsHypervisorRunning();
        if (!hyperV && WindowsHost.IsVirtualizationEnabledInFirmware() == false)
            return (EnableResult.Failed,
                "Virtualization is turned off in this PC's BIOS/UEFI, and only you can turn it on: restart into the BIOS setup " +
                "(usually Del, F2 or F10 while the PC starts) and enable Intel VT-x / Virtualization Technology or AMD SVM.");

        try
        {
            if (!hyperV && await InstallDriverAsync(sdk, progress)) return (EnableResult.Ready, null);

            progress.Report("Turning on the Windows Hypervisor Platform. Windows asks for permission…");
            int code = await RunElevatedAsync("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command",
                 "try { $r = Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All -NoRestart -ErrorAction Stop; " +
                 "if ($r.RestartNeeded) { exit 3010 } else { exit 0 } } catch { exit 1 }"]);
            if (code == 3010)
            {
                File.WriteAllText(RestartMarker, BootTime.Ticks.ToString());
                return (EnableResult.RestartNeeded, null);
            }
            if (code == 0 && (await CheckAsync(sdk)).Ok) return (EnableResult.Ready, null);
            Log.Write($"enabling WHPX ended with {code}");
            return (EnableResult.Failed, code == 0
                ? "The Windows Hypervisor Platform is on, but the emulator still cannot use it. Check that virtualization is enabled in the BIOS/UEFI."
                : "Windows could not turn on the Windows Hypervisor Platform.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: the admin prompt was declined
        {
            return (EnableResult.Declined, null);
        }
    }

    /// <summary>Downloads (if needed) and installs AEHD; true when the emulator can use it afterwards.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<bool> InstallDriverAsync(SdkLocation sdk, IProgress<string> progress)
    {
        var dir = Path.Combine(sdk.SdkRoot, "extras", "google", "Android_Emulator_Hypervisor_Driver");
        var installer = Path.Combine(dir, "silent_install.bat");
        if (!File.Exists(installer))
        {
            progress.Report("Downloading the Android Emulator hypervisor driver…");
            try
            {
                await ImageCatalog.InstallAsync(sdk, "extras/google/Android_Emulator_Hypervisor_Driver",
                    new Progress<InstallProgress>(p => progress.Report($"Hypervisor driver: {p.Phase}")), CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                Log.Write($"hypervisor driver download failed: {ex.Message}");
                return false;
            }
        }
        progress.Report("Installing the Android Emulator hypervisor driver. Windows asks for permission…");
        int code = await RunElevatedAsync("cmd.exe", ["/c", installer], dir);
        var (ok, detail) = await CheckAsync(sdk);
        if (!ok) Log.Write($"hypervisor driver install ended with {code}; emulator says: {detail}");
        return ok;
    }

    /// <summary>Runs a program as administrator without a window and waits for it. Throws Win32Exception 1223 when declined.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task<int> RunElevatedAsync(string exe, string[] args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDir ?? "",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>Restarts Windows now (other apps still get the chance to save).</summary>
    [SupportedOSPlatform("windows")]
    public static void RestartWindows() =>
        Process.Start(new ProcessStartInfo("shutdown.exe") { ArgumentList = { "/r", "/t", "0" }, UseShellExecute = false, CreateNoWindow = true });
}
