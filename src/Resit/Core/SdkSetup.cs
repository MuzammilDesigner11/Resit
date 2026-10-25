using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Win32;
using Resit.Platform;

namespace Resit.Core;

/// <summary>Where the emulator setup is. A null <see cref="Percent"/> means "busy, length unknown".</summary>
public sealed record SetupStatus(string Detail, double? Percent, bool Running, string? Error = null);

/// <summary>
/// Installs what every instance needs (the Android command-line tools, the emulator and
/// platform-tools) from inside Resit, so a fresh install needs no setup script or terminal.
/// It runs in the background from start-up; the Android version itself is downloaded per
/// instance by <see cref="ImageCatalog"/>. One run at a time, shared by every window.
/// </summary>
public static class SdkSetup
{
    private const string RepoUrl = "https://dl.google.com/android/repository";
    // No overall timeout (the tools are 150+ MB); a connection that stops sending is retried instead.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly CancellationTokenSource Shutdown = new();
    private static Task<bool>? _run;

    public static SetupStatus? Status { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="Status"/> changes.</summary>
    public static event Action? StatusChanged;

    public static bool IsRunning => _run is { IsCompleted: false };

    /// <summary>True when instances can be created (any Android version) and started.</summary>
    public static bool IsComplete(SdkLocation sdk) => sdk.IsInstalled && File.Exists(sdk.AndroidCli);

    /// <summary>
    /// Installs whatever is missing, or joins the run already going. True when everything is
    /// there; on failure <see cref="Status"/> says why. Call on the UI thread.
    /// </summary>
    public static Task<bool> RunAsync(SdkLocation sdk)
    {
        if (_run is { IsCompleted: false }) return _run;
        return _run = RunCoreAsync(sdk);
    }

    /// <summary>Stops a running setup because Resit is closing; the next start carries on.</summary>
    public static void Cancel() => Shutdown.Cancel();

    private static async Task<bool> RunCoreAsync(SdkLocation sdk)
    {
        var ct = Shutdown.Token;
        try
        {
            if (!File.Exists(sdk.AndroidCli)) await InstallCommandLineToolsAsync(sdk, ct);
            if (!File.Exists(sdk.Emulator)) await InstallPackageAsync(sdk, "emulator", "the emulator", ct);
            if (!File.Exists(sdk.Adb)) await InstallPackageAsync(sdk, "platform-tools", "platform-tools", ct);
            if (OperatingSystem.IsWindows()) PreferFastGpu(sdk);
            Report(new SetupStatus("Ready", 100, Running: false));
            Log.Write($"SDK ready at {sdk.SdkRoot}");
            return true;
        }
        catch (OperationCanceledException)
        {
            Report(new SetupStatus("Stopped", null, Running: false));
            return false;
        }
        catch (Exception ex)
        {
            // Anything (offline, disk full, a changed download page): report it and offer a retry.
            Log.Write($"SDK setup failed: {ex}");
            var message = ex is HttpRequestException ? "Could not reach Google's download server. Check your internet connection." : ex.Message;
            Report(new SetupStatus("", null, Running: false, Error: message));
            return false;
        }
    }

    private static void Report(SetupStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// The `android` CLI that downloads everything else ships in Google's command-line tools,
    /// a zip unpacked to cmdline-tools/latest.
    /// </summary>
    private static async Task InstallCommandLineToolsAsync(SdkLocation sdk, CancellationToken ct)
    {
        Report(new SetupStatus("Finding the Android command-line tools…", null, Running: true));
        var xml = XDocument.Parse(await Http.GetStringAsync($"{RepoUrl}/repository2-3.xml", ct));
        var hostOs = OS.IsWindows ? "windows" : "linux";
        var archive = xml.Descendants()
            .Where(e => e.Name.LocalName == "remotePackage" && (string?)e.Attribute("path") == "cmdline-tools;latest")
            .OrderBy(p => Child(p, "channelRef")?.Attribute("ref")?.Value is null or "channel-0" ? 0 : 1)   // stable first
            .SelectMany(p => p.Descendants().Where(e => e.Name.LocalName == "archive"))
            .FirstOrDefault(a => Child(a, "host-os")?.Value == hostOs)
            ?? throw new InvalidOperationException("Google's repository lists no command-line tools for this system.");
        var complete = Child(archive, "complete") ?? throw new InvalidOperationException("Unexpected repository format.");
        var file = Child(complete, "url")?.Value ?? throw new InvalidOperationException("Unexpected repository format.");
        var sha1 = Child(complete, "checksum")?.Value;
        long.TryParse(Child(complete, "size")?.Value, out var size);

        // Download and unpack next to the destination, so the final move stays on one drive.
        var toolsDir = Path.Combine(sdk.SdkRoot, "cmdline-tools");
        var staging = Path.Combine(toolsDir, $".resit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var zip = Path.Combine(staging, file);
            var progress = new Progress<(long Done, long Total)>(p => Report(new SetupStatus(
                $"Downloading the command-line tools · {p.Done >> 20} of {p.Total >> 20} MB",
                p.Total > 0 ? 100.0 * p.Done / p.Total : null, Running: true)));
            await DownloadAsync($"{RepoUrl}/{file}", zip, size, sha1, progress, ct);

            Report(new SetupStatus("Unpacking the command-line tools…", null, Running: true));
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging), ct);
            var latest = Path.Combine(toolsDir, "latest");
            Files.DeleteDirectory(latest);   // an older copy without the `android` CLI
            Directory.Move(Path.Combine(staging, "cmdline-tools"), latest);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var bin in Directory.EnumerateFiles(Path.GetDirectoryName(sdk.AndroidCli)!))
                File.SetUnixFileMode(bin, File.GetUnixFileMode(bin) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        if (!File.Exists(sdk.AndroidCli))
            throw new InvalidOperationException($"The command-line tools have no {Path.GetFileName(sdk.AndroidCli)}.");
    }

    /// <summary>Downloads a file with progress, retrying dropped connections, and checks its SHA-1.</summary>
    private static async Task DownloadAsync(string url, string path, long expectedSize, string? sha1,
        IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        const int Attempts = 4;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await Task.Run(async () =>
                {
                    using var stalled = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    try
                    {
                        stalled.CancelAfter(StallTimeout);
                        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stalled.Token);
                        response.EnsureSuccessStatusCode();
                        long total = response.Content.Headers.ContentLength ?? expectedSize;
                        await using var source = await response.Content.ReadAsStreamAsync(stalled.Token);
                        await using var target = File.Create(path);
                        var buffer = new byte[1 << 17];
                        long done = 0;
                        int lastPercent = -1;
                        while (true)
                        {
                            stalled.CancelAfter(StallTimeout);
                            int n = await source.ReadAsync(buffer, stalled.Token);
                            if (n == 0) break;
                            await target.WriteAsync(buffer.AsMemory(0, n), ct);
                            done += n;
                            int percent = total > 0 ? (int)(100 * done / total) : 0;
                            if (percent != lastPercent) progress.Report((done, total));
                            lastPercent = percent;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException("The download stalled.");
                    }
                }, ct);
                break;
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException) && attempt < Attempts && !ct.IsCancellationRequested)
            {
                Log.Write($"download attempt {attempt} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
        }

        if (sha1 is null) return;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct));
        if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded command-line tools are damaged (checksum mismatch). Try again.");
    }

    private static Task InstallPackageAsync(SdkLocation sdk, string package, string name, CancellationToken ct)
    {
        // "Downloading · 12 MB/300 MB" becomes "Downloading the emulator · 12 MB/300 MB".
        var progress = new Progress<InstallProgress>(p =>
        {
            bool busy = p.Percent == 0 && p.Phase.EndsWith('…');
            var verb = p.Phase.StartsWith("Unpacking") ? "Unpacking" : "Downloading";
            Report(busy
                ? new SetupStatus(p.Phase, null, Running: true)
                : new SetupStatus($"{verb} {name}{p.Phase[verb.Length..]}", p.Percent, Running: true));
        });
        return ImageCatalog.InstallAsync(sdk, package, progress, ct);
    }

    /// <summary>
    /// On laptops with two GPUs, runs the emulator on the fast one (a per-user Windows setting,
    /// the same one setup.ps1 writes). A choice the user already made is kept.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void PreferFastGpu(SdkLocation sdk)
    {
        var qemu = Path.Combine(sdk.SdkRoot, "emulator", "qemu", "windows-x86_64");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            foreach (var exe in new[] { sdk.Emulator, Path.Combine(qemu, "qemu-system-x86_64.exe"), Path.Combine(qemu, "qemu-system-x86_64-headless.exe") })
                if (File.Exists(exe) && key.GetValue(exe) is null) key.SetValue(exe, "GpuPreference=2;");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"GPU preference not set: {ex.Message}");
        }
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);
}
