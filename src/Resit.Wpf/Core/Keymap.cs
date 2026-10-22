using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace Resit.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TapControl), "tap")]
[JsonDerivedType(typeof(JoystickControl), "joystick")]
public abstract class KeyControl
{
    /// <summary>Centre of the control, as a fraction (0–1) of the screen as shown.</summary>
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A key that presses one point on the screen for as long as it is held.</summary>
public sealed class TapControl : KeyControl
{
    public Key Key { get; set; } = Key.None;
}

/// <summary>Four keys that drag a virtual joystick from its centre (WASD by default).</summary>
public sealed class JoystickControl : KeyControl
{
    public Key Up { get; set; } = Key.W;
    public Key Left { get; set; } = Key.A;
    public Key Down { get; set; } = Key.S;
    public Key Right { get; set; } = Key.D;

    /// <summary>How far the finger travels, as a fraction of the screen's shorter side.</summary>
    public double Radius { get; set; } = 0.09;
}

public sealed class KeymapProfile
{
    public List<KeyControl> Controls { get; set; } = [];
}

/// <summary>
/// Keymaps are stored per Android package (so a game keeps its layout across instances),
/// with "default" used when no app-specific one exists.
/// </summary>
public static partial class KeymapStore
{
    public const string Default = "default";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string PathFor(string package) =>
        System.IO.Path.Combine(AppPaths.KeymapsDir, SafeName().Replace(package, "_") + ".json");

    public static bool Exists(string package) => File.Exists(PathFor(package));

    public static KeymapProfile Load(string package)
    {
        foreach (var p in new[] { package, Default })
        {
            var path = PathFor(p);
            if (!File.Exists(path)) continue;
            try
            {
                return JsonSerializer.Deserialize<KeymapProfile>(File.ReadAllText(path), Json) ?? new();
            }
            catch (JsonException)
            {
            }
        }
        return new();
    }

    public static void Save(string package, KeymapProfile profile) =>
        File.WriteAllText(PathFor(package), JsonSerializer.Serialize(profile, Json));

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex SafeName();
}
