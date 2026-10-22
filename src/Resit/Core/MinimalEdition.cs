using System.Net.Http;
using System.Text.Json;

namespace Resit.Core;

/// <summary>
/// Trims an AOSP instance down to a browser and a file manager. Apps are removed for the user
/// only (pm uninstall --user 0): they stay on the system image and can be restored with
/// "pm install-existing", and Android's core services are left untouched.
/// </summary>
public static class MinimalEdition
{
    /// <summary>Android's bare WebView test browser, replaced by Firefox.</summary>
    private const string BuiltInBrowser = "org.chromium.webview_shell";

    /// <summary>Kept: launcher, Settings, Files (com.android.documentsui), the keyboard and
    /// everything Android itself depends on. Firefox is added as the browser.</summary>
    public static readonly string[] RemovedPackages =
    [
        // Apps
        "com.android.calendar",
        "com.android.camera2",
        "com.android.contacts",
        "com.android.deskclock",
        "com.android.dialer",
        "com.android.gallery3d",
        "com.android.messaging",
        "com.android.music",
        "com.android.quicksearchbox",
        "com.android.stk",
        "com.android.egg",
        // Idle extras
        "com.android.dreams.basic",
        "com.android.dreams.phototable",
        "com.android.wallpaper.livepicker",
        "com.android.apps.tag",
        "com.android.bips",
        "com.android.printservice.recommendation",
        "com.android.DeviceAsWebcam",
        "com.android.traceur",
        "com.android.emergency",
        "com.android.devicediagnostics",
        "com.android.dynsystem",
        "com.android.eyedropper",
        "com.android.avatarpicker",
        "com.android.cellbroadcastreceiver",
    ];

    /// <summary>
    /// Installs Firefox, removes the extra apps, then checks the result. Returns false if
    /// anything is missing or left over, so the setup runs again on the next start.
    /// </summary>
    public static async Task<bool> ApplyAsync(Adb adb, IProgress<BootProgress> progress, CancellationToken ct)
    {
        const string Stage = "Setting up the Minimal edition";
        var started = DateTime.UtcNow;

        // A real browser first; the bare WebView tester goes only once Firefox is in place.
        bool firefox = (await adb.ShellAsync($"pm path {Firefox.Package}", TimeSpan.FromSeconds(10))).Contains("package:");
        if (!firefox)
        {
            try
            {
                var apk = await Firefox.DownloadAsync(progress, ct);
                progress.Report(new BootProgress("Installing Firefox", 100, DateTime.UtcNow - started));
                firefox = await Firefox.InstallAsync(adb, apk);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
            {
                Log.Write($"firefox download failed: {ex.Message}");
            }
        }
        var toRemove = firefox ? [.. RemovedPackages, BuiltInBrowser] : RemovedPackages;
        progress.Report(new BootProgress(Stage, 0, TimeSpan.Zero));

        // Android 17+ only lets root remove system apps per user. AOSP images allow adb root.
        // adbd restarts to switch, and "wait-for-device" can return before it has, so wait
        // until the shell really runs as root (uid 0).
        await adb.RunAsync(TimeSpan.FromSeconds(20), "root");
        bool rooted = false;
        for (int attempt = 0; attempt < 30 && !rooted; attempt++)
        {
            await Task.Delay(1000, ct);
            try
            {
                await adb.RunAsync(TimeSpan.FromSeconds(10), "wait-for-device");
                rooted = (await adb.ShellAsync("id -u", TimeSpan.FromSeconds(5))).Trim() == "0";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
        if (!rooted)
        {
            Log.Write("minimal: adb root unavailable (not an AOSP image?)");
            return false;
        }
        try
        {
            for (int i = 0; i < toRemove.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new BootProgress(Stage, 5 + 90.0 * i / toRemove.Length, DateTime.UtcNow - started));
                // Failures (a package missing from this Android version) are fine to ignore.
                var output = await adb.ShellAsync($"pm uninstall -k --user 0 {toRemove[i]}", TimeSpan.FromSeconds(20));
                if (!output.Contains("Success")) Log.Write($"minimal: {toRemove[i]}: {output.Trim()}");
            }
        }
        finally
        {
            // Android writes the per-user package state lazily; flush it so a cold boot (or a
            // lost Quick Boot snapshot) keeps the change.
            await adb.ShellAsync("sync", TimeSpan.FromSeconds(30));
            await adb.RunAsync(TimeSpan.FromSeconds(20), "unroot");
            await adb.RunAsync(TimeSpan.FromSeconds(60), "wait-for-device");
        }

        var installed = (await adb.ShellAsync("pm list packages --user 0", TimeSpan.FromSeconds(20)))
            .Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:")).Select(l => l["package:".Length..]).ToHashSet();
        var left = toRemove.Where(installed.Contains).ToList();
        if (left.Count > 0) Log.Write($"minimal: still installed: {string.Join(", ", left)}");
        progress.Report(new BootProgress(Stage, 100, DateTime.UtcNow - started));
        return left.Count == 0 && installed.Contains(Firefox.Package);
    }
}
