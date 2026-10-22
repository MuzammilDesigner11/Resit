using System.Text.RegularExpressions;

namespace Resit.Core;

/// <summary>An installed system image, e.g. system-images\android-37.0\google_apis_playstore\x86_64.</summary>
public sealed partial record SystemImage(string ApiId, string Tag, string Abi, string RelativePath)
{
    /// <summary>"35" or "37.0".</summary>
    public string ApiLevel => ApiId.StartsWith("android-") ? ApiId["android-".Length..] : ApiId;

    public bool PlayStore => Tag.Contains("playstore");

    public string TagDisplay => Tag switch
    {
        "google_apis_playstore" => "Google Play",
        "google_apis" => "Google APIs",
        "default" => "AOSP",
        _ => Tag,
    };

    public string AndroidVersion => AndroidVersionName(ApiLevel);

    public string DisplayName => $"Android {AndroidVersion} · {TagDisplay} · {Abi}";

    public override string ToString() => DisplayName;

    public static string AndroidVersionName(string apiLevel)
    {
        var m = LeadingNumber().Match(apiLevel);
        if (!m.Success) return apiLevel;
        int api = int.Parse(m.Value);
        return api switch
        {
            >= 33 => (api - 20).ToString(),   // 33 = Android 13 ... 37 = Android 17
            32 => "12L",
            >= 29 => (api - 19).ToString(),   // 29 = Android 10 ... 31 = Android 12
            28 => "9",
            27 => "8.1",
            26 => "8.0",
            _ => $"API {api}",
        };
    }

    /// <summary>Installed x86_64 images, newest first.</summary>
    public static List<SystemImage> FindInstalled(string sdkRoot)
    {
        var result = new List<SystemImage>();
        var root = Path.Combine(sdkRoot, "system-images");
        if (!Directory.Exists(root)) return result;
        foreach (var api in Directory.EnumerateDirectories(root))
        foreach (var tag in Directory.EnumerateDirectories(api))
        foreach (var abi in Directory.EnumerateDirectories(tag))
        {
            if (!File.Exists(Path.Combine(abi, "system.img"))) continue;
            var a = Path.GetFileName(abi);
            if (a != "x86_64") continue;   // ARM images run through translation and are far slower
            result.Add(new SystemImage(Path.GetFileName(api), Path.GetFileName(tag), a,
                Path.GetRelativePath(sdkRoot, abi) + Path.DirectorySeparatorChar));
        }
        return result.OrderByDescending(i => Version.TryParse(Normalize(i.ApiLevel), out var v) ? v : new Version(0, 0)).ToList();
    }

    /// <summary>Resolves an AVD's image.sysdir.1 value.</summary>
    public static SystemImage? FromSysDir(string? sysDir)
    {
        if (string.IsNullOrWhiteSpace(sysDir)) return null;
        var parts = sysDir.Replace('/', '\\').Trim('\\').Split('\\');
        if (parts.Length < 4) return null;
        return new SystemImage(parts[^3], parts[^2], parts[^1], sysDir);
    }

    private static string Normalize(string api)
    {
        var m = LeadingVersion().Match(api);
        return m.Success ? (m.Value.Contains('.') ? m.Value : m.Value + ".0") : "0.0";
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^\d+(\.\d+)?")]
    private static partial Regex LeadingVersion();
}
