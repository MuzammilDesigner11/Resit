using Avalonia.Input;

namespace Resit.Core;

/// <summary>
/// Turns key presses into multi-touch input according to a <see cref="KeymapProfile"/>.
/// Each control gets its own finger, so a held joystick and several taps work together.
/// </summary>
public sealed class KeymapEngine(EmulatorClient client, Func<ScreenGeometry> geometry)
{
    private const int TapIdBase = 100;
    private const int JoystickIdBase = 200;

    private readonly HashSet<Key> _held = [];
    private readonly HashSet<int> _down = [];
    private KeymapProfile _profile = new();

    public bool Enabled { get; set; } = true;

    public KeymapProfile Profile
    {
        get => _profile;
        set
        {
            ReleaseAll();
            _profile = value;
        }
    }

    /// <summary>Returns true when the key belongs to a control (and should not reach Android).</summary>
    public bool HandleKey(Key key, bool isDown)
    {
        if (!Enabled) return false;
        bool changed = isDown ? _held.Add(key) : _held.Remove(key);
        bool mapped = false;

        for (int i = 0; i < _profile.Controls.Count; i++)
        {
            switch (_profile.Controls[i])
            {
                case TapControl tap when tap.Key == key:
                    mapped = true;
                    if (changed) Touch(TapIdBase + i, tap.X, tap.Y, isDown);
                    break;

                case JoystickControl stick when key == stick.Up || key == stick.Down || key == stick.Left || key == stick.Right:
                    mapped = true;
                    if (changed) UpdateJoystick(JoystickIdBase + i, stick);
                    break;
            }
        }
        return mapped;
    }

    /// <summary>Lifts every finger, e.g. when the window loses focus mid-press.</summary>
    public void ReleaseAll()
    {
        _held.Clear();
        foreach (var id in _down.ToList()) Lift(id);
    }

    private void UpdateJoystick(int id, JoystickControl stick)
    {
        double dx = (_held.Contains(stick.Right) ? 1 : 0) - (_held.Contains(stick.Left) ? 1 : 0);
        double dy = (_held.Contains(stick.Down) ? 1 : 0) - (_held.Contains(stick.Up) ? 1 : 0);
        if (dx == 0 && dy == 0)
        {
            if (_down.Contains(id)) Touch(id, stick.X, stick.Y, false);
            return;
        }

        // Most game joysticks anchor where the finger lands, so touch the centre first, then push.
        if (!_down.Contains(id)) Touch(id, stick.X, stick.Y, true);

        var g = geometry();
        double len = Math.Sqrt(dx * dx + dy * dy);
        double radiusPx = stick.Radius * Math.Min(g.ShownWidth, g.ShownHeight);
        double u = stick.X + dx / len * radiusPx / g.ShownWidth;
        double v = stick.Y + dy / len * radiusPx / g.ShownHeight;
        Touch(id, Math.Clamp(u, 0, 1), Math.Clamp(v, 0, 1), true);
    }

    private void Touch(int id, double u, double v, bool down)
    {
        var (x, y) = geometry().ToDevice(u, v);
        client.Touch(id, x, y, down);
        if (down) _down.Add(id); else _down.Remove(id);
    }

    private void Lift(int id)
    {
        client.Touch(id, 0, 0, false);
        _down.Remove(id);
    }
}

/// <summary>
/// The device's native panel size and how far it is rotated on screen. Touches are sent in
/// native panel pixels, while controls are placed on the screen as the user sees it.
/// </summary>
public readonly record struct ScreenGeometry(int NativeWidth, int NativeHeight, int QuarterTurns)
{
    public bool IsSideways => QuarterTurns % 2 != 0;
    public int ShownWidth => IsSideways ? NativeHeight : NativeWidth;
    public int ShownHeight => IsSideways ? NativeWidth : NativeHeight;
    public double ShownAspect => (double)ShownWidth / ShownHeight;

    /// <summary>Maps a point given as fractions of the shown screen to native panel pixels.</summary>
    public (int X, int Y) ToDevice(double u, double v)
    {
        (double nx, double ny) = (((QuarterTurns % 4) + 4) % 4) switch
        {
            1 => (1 - v, u),
            2 => (1 - u, 1 - v),
            3 => (v, 1 - u),
            _ => (u, v),
        };
        return ((int)Math.Round(nx * (NativeWidth - 1)), (int)Math.Round(ny * (NativeHeight - 1)));
    }

    /// <summary>Quarter turns for an emulator rotation angle in degrees (0, 90, 180, -90).</summary>
    public static int QuarterTurnsFor(double degrees) => (int)Math.Round(((degrees % 360) + 360) % 360 / 90.0) % 4;
}
