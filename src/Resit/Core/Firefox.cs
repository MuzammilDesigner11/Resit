using System.Net.Http;
using System.Text.Json;

namespace Resit.Core;

/// <summary>
/// Mozilla's official stable Firefox for Android (x86_64), the Minimal edition's browser.
/// Chrome is only distributed through the Play Store, so it cannot be installed there.
/// </summary>
public static class Firefox
{
    public const string Package = "org.mozilla.firefox";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static string CacheDir => Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "cache")).FullName;

    /// <summary>Downloads the current stable APK (cached between instances) and returns its path.</summary>
    public static async Task<string> DownloadAsync(IProgress<BootProgress> progress, CancellationToken ct)
    {
        using var versions = JsonDocument.Parse(await Http.GetStringAsync("https://product-details.mozilla.org/1.0/mobile_versions.json", ct));
        var version = versions.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidOperationException("Mozilla did not report a Firefox version.");

        var path = Path.Combine(CacheDir, $"fenix-{version}-x86_64.apk");
        if (File.Exists(path)) return path;

        var url = $"https://archive.mozilla.org/pub/fenix/releases/{version}/android/fenix-{version}-android-x86_64/fenix-{version}.multi.android-x86_64.apk";
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        var started = DateTime.UtcNow;
        var part = path + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(part))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            var lastReport = DateTime.MinValue;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                {
                    lastReport = DateTime.UtcNow;
                    var label = total > 0 ? $"Downloading Firefox {version} · {done >> 20} / {total >> 20} MB" : $"Downloading Firefox {version}";
                    progress.Report(new BootProgress(label, total > 0 ? 100.0 * done / total : 0, DateTime.UtcNow - started));
                }
            }
        }
        File.Move(part, path, overwrite: true);
        // Keep only the newest download.
        foreach (var old in Directory.EnumerateFiles(CacheDir, "fenix-*.apk").Where(f => f != path)) File.Delete(old);
        return path;
    }

    /// <summary>Removes the downloaded APK, for when no Minimal instance needs it any more.</summary>
    public static void ClearCache()
    {
        var dir = Path.Combine(AppPaths.DataDir, "cache");
        if (!Directory.Exists(dir)) return;
        foreach (var apk in Directory.EnumerateFiles(dir, "fenix-*")) File.Delete(apk);
    }

    /// <summary>Installs Firefox and makes it the default browser. Returns false if the install failed.</summary>
    public static async Task<bool> InstallAsync(Adb adb, string apk)
    {
        var (ok, message) = await adb.InstallAsync(apk);
        if (!ok)
        {
            Log.Write($"firefox install failed: {message}");
            return false;
        }
        await adb.ShellAsync($"cmd role add-role-holder android.app.role.BROWSER {Package} 0", TimeSpan.FromSeconds(15));
        return true;
    }
}
