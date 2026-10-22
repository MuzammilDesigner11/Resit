using System.Globalization;
using System.Text;

namespace Resit.Core;

/// <summary>
/// The emulator's key=value files (config.ini, &lt;name&gt;.ini). Unknown keys, comments and
/// order are preserved so files stay compatible with Android Studio.
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines;

    public string FilePath { get; }

    private IniFile(string path, List<string> lines)
    {
        FilePath = path;
        _lines = lines;
    }

    public static IniFile Load(string path) =>
        new(path, File.Exists(path) ? File.ReadAllLines(path).ToList() : []);

    public string? Get(string key)
    {
        int i = IndexOf(key);
        return i < 0 ? null : _lines[i][(_lines[i].IndexOf('=') + 1)..].Trim();
    }

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback) => Get(key)?.ToLowerInvariant() switch
    {
        "yes" or "true" or "1" => true,
        "no" or "false" or "0" => false,
        _ => fallback,
    };

    /// <summary>Reads sizes such as "4096", "4096M", "4G" or "800MB" as megabytes.</summary>
    public int GetSizeMB(string key, int fallback)
    {
        var raw = Get(key)?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(raw)) return fallback;
        raw = raw.TrimEnd('B');
        double scale = 1;
        if (raw.EndsWith('G')) { scale = 1024; raw = raw[..^1]; }
        else if (raw.EndsWith('M')) { raw = raw[..^1]; }
        else if (raw.EndsWith('K')) { scale = 1.0 / 1024; raw = raw[..^1]; }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)(v * scale) : fallback;
    }

    public void Set(string key, object value)
    {
        var text = value switch
        {
            bool b => b ? "yes" : "no",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        var line = $"{key}={text}";
        int i = IndexOf(key);
        if (i < 0) _lines.Add(line); else _lines[i] = line;
    }

    public void Remove(string key)
    {
        int i = IndexOf(key);
        if (i >= 0) _lines.RemoveAt(i);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllLines(FilePath, _lines, new UTF8Encoding(false));
    }

    private int IndexOf(string key)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            int eq = line.IndexOf('=');
            if (eq > 0 && line.AsSpan(0, eq).Trim().SequenceEqual(key)) return i;
        }
        return -1;
    }
}
