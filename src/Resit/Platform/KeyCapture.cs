using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Input;
using Avalonia.Threading;
using Resit.Core;

namespace Resit.Platform;

/// <summary>
/// One key going down or up while the player is in front. <see cref="Code"/> is the key's
/// physical code in the form the emulator accepts on this OS (see <see cref="KeyCapture.CodeType"/>).
/// </summary>
public readonly record struct KeyStroke(Key Key, int Code, bool IsDown, KeyModifiers Modifiers);

/// <summary>
/// Catches the keyboard while the player window is in front. Game controls need this because
/// keyboard focus sits inside the emulator's own (foreign-process) window, so the UI toolkit
/// never sees those key events. Handlers run on the UI thread.
/// </summary>
public abstract class KeyCapture : IDisposable
{
    /// <summary>Return true when Resit handled the key (Windows then never passes it on).</summary>
    public delegate bool KeyHandler(KeyStroke stroke);

    /// <summary>How <see cref="KeyStroke.Code"/> is meant: Windows scan codes or Linux evdev codes.</summary>
    public static Android.Emulation.Control.KeyboardEvent.Types.KeyCodeType CodeType =>
        OS.IsWindows
            ? Android.Emulation.Control.KeyboardEvent.Types.KeyCodeType.Win
            : Android.Emulation.Control.KeyboardEvent.Types.KeyCodeType.Evdev;

    /// <param name="topLevel">The player window's native handle.</param>
    /// <param name="isActive">Whether keys should go to the game right now.</param>
    public static KeyCapture Start(IntPtr topLevel, Func<bool> isActive, KeyHandler handler) =>
        OperatingSystem.IsWindows() ? new WindowsKeyCapture(isActive, handler) :
        OperatingSystem.IsLinux() ? new X11KeyCapture(topLevel, isActive, handler) :
        throw new PlatformNotSupportedException();

    public abstract void Dispose();
}

/// <summary>Low-level keyboard hook; it runs on the thread that installed it (the UI thread).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsKeyCapture : KeyCapture
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private readonly HookProc _proc;   // kept alive for as long as the hook is installed
    private readonly Func<bool> _isActive;
    private readonly KeyHandler _handler;
    private IntPtr _hook;

    public WindowsKeyCapture(Func<bool> isActive, KeyHandler handler)
    {
        _isActive = isActive;
        _handler = handler;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException($"SetWindowsHookEx failed ({Marshal.GetLastWin32Error()})");
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            // Injected keys are handled too, so macro tools (AutoHotkey etc.) can drive game
            // controls. Resit itself never injects keystrokes, so this cannot loop.
            if ((down || up) && _isActive())
            {
                var stroke = new KeyStroke(WindowsKeys.FromVirtualKey((int)data.vkCode), ScanCodeOf(data), down, WindowsKeys.Modifiers());
                if (_handler(stroke)) return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>
    /// The key's Windows scan code (its physical position), with 0xE000 added for extended keys
    /// such as the arrows. This is what the emulator's "Win" key codes mean; sending the
    /// virtual-key code instead typed the wrong keys (Backspace, 0x08, came out as "7").
    /// </summary>
    private static int ScanCodeOf(KBDLLHOOKSTRUCT data)
    {
        const uint LLKHF_EXTENDED = 0x01;
        const uint MAPVK_VK_TO_VSC_EX = 4;
        if (data.scanCode == 0)   // some injected keys carry no scan code
            return (int)MapVirtualKey(data.vkCode, MAPVK_VK_TO_VSC_EX);
        return (int)data.scanCode | ((data.flags & LLKHF_EXTENDED) != 0 ? 0xE000 : 0);
    }

    public override void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}

/// <summary>
/// A passive grab of every key on the player window: while focus is anywhere inside it
/// (including the embedded emulator window), key events come to this connection instead of
/// the focused window. Window-manager shortcuts such as Alt+Tab are grabbed on the root window,
/// which takes precedence, so they keep working. Events are read on a background thread and
/// handed to the UI thread.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11KeyCapture : KeyCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events, revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, nuint count, int timeout);

    private readonly IntPtr _window;
    private readonly Func<bool> _isActive;
    private readonly KeyHandler _handler;
    private readonly Thread _thread;
    private volatile bool _stop;

    public X11KeyCapture(IntPtr window, Func<bool> isActive, KeyHandler handler)
    {
        _window = window;
        _isActive = isActive;
        _handler = handler;
        _thread = new Thread(Run) { IsBackground = true, Name = "Resit keyboard" };
        _thread.Start();
    }

    private unsafe void Run()
    {
        var display = X11.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            Log.Write("x11 keys: cannot open display");
            return;
        }
        try
        {
            // Without this, a held key arrives as release/press pairs and games see it flicker.
            X11.XkbSetDetectableAutoRepeat(display, true, out _);
            X11.XGrabKey(display, X11.AnyKey, X11.AnyModifier, _window, false, X11.GrabModeAsync, X11.GrabModeAsync);
            X11.XSync(display, false);

            var fd = new PollFd { fd = X11.XConnectionNumber(display), events = 1 /* POLLIN */ };
            var ev = stackalloc byte[192];   // sizeof(XEvent)
            while (!_stop)
            {
                while (X11.XPending(display) > 0)
                {
                    X11.XNextEvent(display, (IntPtr)ev);
                    int type = *(int*)ev;
                    if (type is not (X11.KeyPress or X11.KeyRelease)) continue;
                    // XKeyEvent on 64-bit: state at byte 80, keycode at 84.
                    uint state = *(uint*)(ev + 80);
                    uint keycode = *(uint*)(ev + 84);
                    var key = LinuxKeys.FromKeysym(X11.XkbKeycodeToKeysym(display, (byte)keycode, 0, 0).ToInt64());
                    var stroke = new KeyStroke(key, (int)keycode - 8, type == X11.KeyPress, LinuxKeys.Modifiers(state));
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_isActive()) _handler(stroke);
                    });
                }
                poll(ref fd, 1, 100);
            }
            X11.XUngrabKey(display, X11.AnyKey, X11.AnyModifier, _window);
            X11.XSync(display, false);
        }
        catch (Exception ex)
        {
            Log.Write($"x11 keys: {ex}");
        }
        finally
        {
            X11.XCloseDisplay(display);
        }
    }

    public override void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}

/// <summary>Windows virtual-key codes to Avalonia keys (same names WPF used, so saved keymaps still load).</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsKeys
{
    public static Key FromVirtualKey(int vk) => vk switch
    {
        >= 0x41 and <= 0x5A => Key.A + (vk - 0x41),
        >= 0x30 and <= 0x39 => Key.D0 + (vk - 0x30),
        >= 0x60 and <= 0x69 => Key.NumPad0 + (vk - 0x60),
        >= 0x70 and <= 0x87 => Key.F1 + (vk - 0x70),
        0x08 => Key.Back,
        0x09 => Key.Tab,
        0x0D => Key.Return,
        0x10 => Key.LeftShift,
        0x11 => Key.LeftCtrl,
        0x12 => Key.LeftAlt,
        0x13 => Key.Pause,
        0x14 => Key.CapsLock,
        0x1B => Key.Escape,
        0x20 => Key.Space,
        0x21 => Key.PageUp,
        0x22 => Key.PageDown,
        0x23 => Key.End,
        0x24 => Key.Home,
        0x25 => Key.Left,
        0x26 => Key.Up,
        0x27 => Key.Right,
        0x28 => Key.Down,
        0x2C => Key.PrintScreen,
        0x2D => Key.Insert,
        0x2E => Key.Delete,
        0x5B => Key.LWin,
        0x5C => Key.RWin,
        0x5D => Key.Apps,
        0x6A => Key.Multiply,
        0x6B => Key.Add,
        0x6D => Key.Subtract,
        0x6E => Key.Decimal,
        0x6F => Key.Divide,
        0x90 => Key.NumLock,
        0x91 => Key.Scroll,
        0xA0 => Key.LeftShift,
        0xA1 => Key.RightShift,
        0xA2 => Key.LeftCtrl,
        0xA3 => Key.RightCtrl,
        0xA4 => Key.LeftAlt,
        0xA5 => Key.RightAlt,
        0xBA => Key.OemSemicolon,
        0xBB => Key.OemPlus,
        0xBC => Key.OemComma,
        0xBD => Key.OemMinus,
        0xBE => Key.OemPeriod,
        0xBF => Key.OemQuestion,
        0xC0 => Key.OemTilde,
        0xDB => Key.OemOpenBrackets,
        0xDC => Key.OemPipe,
        0xDD => Key.OemCloseBrackets,
        0xDE => Key.OemQuotes,
        0xE2 => Key.OemBackslash,
        _ => Key.None,
    };

    public static KeyModifiers Modifiers()
    {
        var m = KeyModifiers.None;
        if (Win32.IsKeyDown(0x10)) m |= KeyModifiers.Shift;
        if (Win32.IsKeyDown(0x11)) m |= KeyModifiers.Control;
        if (Win32.IsKeyDown(0x12)) m |= KeyModifiers.Alt;
        if (Win32.IsKeyDown(0x5B) || Win32.IsKeyDown(0x5C)) m |= KeyModifiers.Meta;
        return m;
    }
}

/// <summary>X keysyms (unshifted, first layout group) to Avalonia keys.</summary>
internal static class LinuxKeys
{
    public static Key FromKeysym(long sym) => sym switch
    {
        >= 0x61 and <= 0x7A => Key.A + (int)(sym - 0x61),
        >= 0x41 and <= 0x5A => Key.A + (int)(sym - 0x41),
        >= 0x30 and <= 0x39 => Key.D0 + (int)(sym - 0x30),
        >= 0xFFB0 and <= 0xFFB9 => Key.NumPad0 + (int)(sym - 0xFFB0),
        >= 0xFFBE and <= 0xFFD5 => Key.F1 + (int)(sym - 0xFFBE),
        0x20 => Key.Space,
        0x27 => Key.OemQuotes,
        0x2C => Key.OemComma,
        0x2D => Key.OemMinus,
        0x2E => Key.OemPeriod,
        0x2F => Key.OemQuestion,
        0x3B => Key.OemSemicolon,
        0x3D => Key.OemPlus,
        0x5B => Key.OemOpenBrackets,
        0x5C => Key.OemPipe,
        0x5D => Key.OemCloseBrackets,
        0x60 => Key.OemTilde,
        0x3C => Key.OemBackslash,   // the extra key left of Z on ISO keyboards
        0xFF08 => Key.Back,
        0xFF09 => Key.Tab,
        0xFF0D or 0xFF8D => Key.Return,
        0xFF13 => Key.Pause,
        0xFF14 => Key.Scroll,
        0xFF1B => Key.Escape,
        0xFF50 => Key.Home,
        0xFF51 => Key.Left,
        0xFF52 => Key.Up,
        0xFF53 => Key.Right,
        0xFF54 => Key.Down,
        0xFF55 => Key.PageUp,
        0xFF56 => Key.PageDown,
        0xFF57 => Key.End,
        0xFF61 => Key.PrintScreen,
        0xFF63 => Key.Insert,
        0xFF67 => Key.Apps,
        0xFF7F => Key.NumLock,
        0xFFAA => Key.Multiply,
        0xFFAB => Key.Add,
        0xFFAD => Key.Subtract,
        0xFFAE => Key.Decimal,
        0xFFAF => Key.Divide,
        0xFFE1 => Key.LeftShift,
        0xFFE2 => Key.RightShift,
        0xFFE3 => Key.LeftCtrl,
        0xFFE4 => Key.RightCtrl,
        0xFFE5 => Key.CapsLock,
        0xFFE9 => Key.LeftAlt,
        0xFFEA or 0xFE03 => Key.RightAlt,   // Alt_R, or AltGr (ISO_Level3_Shift)
        0xFFEB => Key.LWin,
        0xFFEC => Key.RWin,
        0xFFFF => Key.Delete,
        _ => Key.None,
    };

    public static KeyModifiers Modifiers(uint state)
    {
        var m = KeyModifiers.None;
        if ((state & 1) != 0) m |= KeyModifiers.Shift;
        if ((state & 4) != 0) m |= KeyModifiers.Control;
        if ((state & 8) != 0) m |= KeyModifiers.Alt;
        if ((state & 64) != 0) m |= KeyModifiers.Meta;
        return m;
    }
}
