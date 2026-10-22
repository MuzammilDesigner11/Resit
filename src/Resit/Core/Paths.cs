using System.Text.Json;
using Resit.Platform;

namespace Resit.Core;

/// <summary>Where the Android SDK and the AVDs live.</summary>
public sealed class SdkLocation
{
    public required string SdkRoot { get; init; }
    public required string AvdHome { get; init; }

    public string Emulator => Path.Combine(SdkRoot, "emulator", OS.Exe("emulator"));
    public string Adb => Path.Combine(SdkRoot, "platform-tools", OS.Exe("adb"));
    /// <summary>Google's `android` command-line tool, which downloads SDK packages.</summary>
    public string AndroidCli => Path.Combine(SdkRoot, "cmdline-tools", "latest", "bin", OS.Exe("android"));
    public bool IsInstalled => File.Exists(Emulator) && File.Exists(Adb);

    /// <summary>Why <see cref="IsInstalled"/> is false, for the "SDK not found" screen and the log.</summary>
    public string MissingReason()
    {
        var missing = new[] { Emulator, Adb }.Where(f => !File.Exists(f)).ToList();
        if (missing.Count == 0) return "";
        var detail = string.Join(", ", missing.Select(f => Path.GetRelativePath(SdkRoot, f)));
        try
        {
            // Distinguish "not there" from "there but unreadable" (permissions, a cleaner holding it).
            if (!Directory.Exists(SdkRoot)) return $"The folder {SdkRoot} does not exist.";
            foreach (var f in missing) _ = new FileInfo(f).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Missing or unreadable: {detail} ({ex.Message})";
        }
        return $"Missing: {detail}";
    }

    /// <summary>
    /// Prefers resit.local.json (written by the setup script) next to the app or in any parent
    /// folder, then the copy in Resit's settings folder (for an installed app), then the standard
    /// Android environment variables, then the Android Studio defaults (%LOCALAPPDATA%\Android\Sdk
    /// on Windows, ~/Android/Sdk on Linux).
    /// </summary>
    public static SdkLocation Detect()
    {
        var state = FindUpwards("resit.local.json")
            ?? new[] { Path.Combine(AppPaths.DataDir, "resit.local.json") }.FirstOrDefault(File.Exists);
        if (state is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(state));
                var root = doc.RootElement;
                return new SdkLocation
                {
                    SdkRoot = root.GetProperty("sdkRoot").GetString()!,
                    AvdHome = root.GetProperty("avdHome").GetString()!,
                };
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Fall through to the defaults.
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string sdk = Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
            ?? (OS.IsWindows
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
                : Path.Combine(home, "Android", "Sdk"));
        string avd = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME")
            ?? Path.Combine(home, ".android", "avd");
        return new SdkLocation { SdkRoot = sdk, AvdHome = avd };
    }

    /// <summary>Finds a file in the app folder or one of its parents (covers running from a repo checkout).</summary>
    public static string? FindUpwards(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

public static class AppPaths
{
    public static string DataDir { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Resit"));

    public static string KeymapsDir { get; } = Ensure(Path.Combine(DataDir, "keymaps"));

    public static string ScreenshotsDir => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Resit"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
