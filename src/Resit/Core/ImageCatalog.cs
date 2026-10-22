using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Resit.Core;

/// <summary>What an instance is built from: Google Play, or a stripped-down AOSP system.</summary>
public enum Edition
{
    /// <summary>Full Android with Google Play (google_apis_playstore image).</summary>
    GooglePlay,
    /// <summary>AOSP image trimmed to a browser and a file manager. No Google services.</summary>
    Minimal,
}

/// <summary>A stable x86_64 system image Google publishes, installed or not.</summary>
public sealed record AvailableImage(string ApiLevel, string Tag, long SizeBytes, bool Installed)
{
    public string Path => $"system-images/android-{ApiLevel}/{Tag}/x86_64";
    public string AndroidVersion => SystemImage.AndroidVersionName(ApiLevel);
    public string Label => $"Android {AndroidVersion} (API {ApiLevel})" + (Installed ? " · installed" : $" · {SizeBytes / (1024 * 1024)} MB download");
    public override string ToString() => Label;

    public static string TagFor(Edition edition) => edition == Edition.Minimal ? "default" : "google_apis_playstore";
}

public sealed record InstallProgress(string Phase, double Percent);

/// <summary>Lists Google's system images and installs them with the Android CLI, reporting progress.</summary>
public static partial class ImageCatalog
{
    private const string RepoUrl = "https://dl.google.com/android/repository/sys-img";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Stable x86_64 images for an edition, newest first. Falls back to installed ones when offline.</summary>
    public static async Task<List<AvailableImage>> ListAsync(SdkLocation sdk, Edition edition, CancellationToken ct = default)
    {
        var tag = AvailableImage.TagFor(edition);
        var installed = SystemImage.FindInstalled(sdk.SdkRoot).Where(i => i.Tag == tag).Select(i => i.ApiLevel).ToHashSet();
        var result = new List<AvailableImage>();
        try
        {
            // Google files AOSP ("default") images under sys-img/android/.
            var folder = tag == "default" ? "android" : tag;
            var xml = XDocument.Parse(await Http.GetStringAsync($"{RepoUrl}/{folder}/sys-img2-3.xml", ct));
            foreach (var pkg in xml.Descendants().Where(e => e.Name.LocalName == "remotePackage"))
            {
                var m = StablePath().Match((string?)pkg.Attribute("path") ?? "");
                if (!m.Success || m.Groups[2].Value != tag) continue;
                var channel = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "channelRef")?.Attribute("ref")?.Value;
                if (channel is not null && channel != "channel-0") continue;
                var size = pkg.Descendants().FirstOrDefault(e => e.Name.LocalName == "size")?.Value;
                var api = m.Groups[1].Value;
                result.Add(new AvailableImage(api, tag, long.TryParse(size, out var s) ? s : 0, installed.Contains(api)));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            Log.Write($"image list failed: {ex.Message}");
        }
        foreach (var api in installed.Where(a => result.All(r => r.ApiLevel != a)))
            result.Add(new AvailableImage(api, tag, 0, true));

        return result
            .Where(r => int.TryParse(r.ApiLevel.Split('.')[0], out var major) && major >= 30)   // Android 11+
            .OrderByDescending(r => Version.TryParse(r.ApiLevel.Contains('.') ? r.ApiLevel : r.ApiLevel + ".0", out var v) ? v : new Version())
            .ToList();
    }

    /// <summary>
    /// Downloads and unpacks an image (or any SDK package) through the Android CLI, retrying when
    /// the connection drops (the CLI resumes from what it already has).
    /// </summary>
    public static async Task InstallAsync(SdkLocation sdk, string packagePath, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        const int Attempts = 4;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                progress.Report(new InstallProgress(attempt == 1 ? "Preparing download…" : $"Connection dropped, retrying ({attempt}/{Attempts})…", 0));
                await InstallOnceAsync(sdk, packagePath, progress, ct);
                return;
            }
            catch (InvalidOperationException ex) when (attempt < Attempts && !ct.IsCancellationRequested)
            {
                Log.Write($"install attempt {attempt} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
        }
    }

    private static async Task InstallOnceAsync(SdkLocation sdk, string packagePath, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var cli = sdk.AndroidCli;
        if (!File.Exists(cli)) throw new FileNotFoundException($"The Android command-line tools are missing. Run {Platform.OS.SetupScript} first.", cli);

        var psi = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--no-metrics", $"--sdk={sdk.SdkRoot}", "sdk", "install", packagePath }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the Android CLI.");
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errors = p.StandardError.ReadToEndAsync(CancellationToken.None);

        // Progress lines are redrawn with carriage returns, so read character by character.
        var line = new StringBuilder();
        var buffer = new char[512];
        int n;
        while ((n = await p.StandardOutput.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                char c = buffer[i];
                if (c is '\r' or '\n')
                {
                    Report(line.ToString(), progress);
                    line.Clear();
                }
                else line.Append(c);
            }
        }
        Report(line.ToString(), progress);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Download failed ({p.ExitCode}). {(await errors).Trim()}");
    }

    private static void Report(string line, IProgress<InstallProgress> progress)
    {
        var m = Percent().Match(line);
        if (!m.Success) return;
        var phase = line.Contains("Unzipping", StringComparison.OrdinalIgnoreCase) ? "Unpacking" : "Downloading";
        var detail = Sizes().Match(line);
        var eta = Eta().Match(line);
        var text = phase + (detail.Success ? $" · {Regex.Replace(detail.Groups[1].Value.Trim(), @"\s+", " ")}" : "")
                         + (eta.Success && phase == "Downloading" ? $" · {eta.Groups[1].Value} left" : "");
        progress.Report(new InstallProgress(text, double.Parse(m.Groups[1].Value)));
    }

    [GeneratedRegex(@"ETA:\s*(\d+s)")]
    private static partial Regex Eta();

    [GeneratedRegex(@"^system-images;android-(\d+(?:\.\d+)?);([a-z_]+);x86_64$")]
    private static partial Regex StablePath();

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"\(([^)]*/[^)]*)\)")]
    private static partial Regex Sizes();
}
