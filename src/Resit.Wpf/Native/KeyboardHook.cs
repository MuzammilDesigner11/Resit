using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Resit.Native;

/// <summary>
/// Low-level keyboard hook. Game controls need it because keyboard focus sits inside the
/// emulator's own (foreign-process) window, so WPF never sees those key events.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>Return true to swallow the key (Resit handles it; Windows and the emulator never see it).</summary>
    public delegate bool KeyHandler(Key key, int scanCode, bool isDown);

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

    private readonly HookProc _proc;   // kept alive for as long as the hook is installed
    private readonly Func<bool> _isActive;
    private readonly KeyHandler _handler;
    private IntPtr _hook;

    public KeyboardHook(Func<bool> isActive, KeyHandler handler)
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
                var key = KeyInterop.KeyFromVirtualKey((int)data.vkCode);
                if (_handler(key, ScanCodeOf(data), down)) return new IntPtr(1);
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

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
