using System.Diagnostics;
using System.Text;

namespace Resit.Core;

public static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string exe, IEnumerable<string> args,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (p.ExitCode, (await stdout) + (await stderr));
    }
}

/// <summary>adb, bound to one emulator.</summary>
public sealed class Adb(string adbPath, string serial)
{
    public Task<(int ExitCode, string Output)> RunAsync(TimeSpan? timeout, params string[] args) =>
        ProcessRunner.RunAsync(adbPath, ["-s", serial, .. args], timeout);

    public async Task<string> ShellAsync(string command, TimeSpan? timeout = null) =>
        (await RunAsync(timeout ?? TimeSpan.FromSeconds(15), "shell", command)).Output;

    /// <summary>Installs or updates an APK, granting its runtime permissions.</summary>
    public async Task<(bool Ok, string Message)> InstallAsync(string apkPath)
    {
        var (code, output) = await RunAsync(TimeSpan.FromMinutes(5), "install", "-r", "-g", apkPath);
        var ok = code == 0 && output.Contains("Success");
        var message = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        return (ok, message);
    }

    /// <summary>Asks the emulator to exit; with Quick Boot on it saves a snapshot first.</summary>
    public Task EmuKillAsync() => RunAsync(TimeSpan.FromSeconds(10), "emu", "kill");
}
