using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Resit.Native;

namespace Resit.Views;

/// <summary>
/// Hosts the emulator's own window inside WPF. The emulator keeps rendering straight to its
/// window with the GPU, so embedding costs no performance (unlike streaming frames).
/// </summary>
internal sealed class EmulatorHost : HwndHost
{
    // The emulator snaps its window to the display's aspect ratio after a resize, so a few
    // pixels of difference from our container are expected and must not trigger a new resize
    // (that would loop forever).
    private const int SizeTolerance = 6;

    private IntPtr _container;
    private IntPtr _emulator;

    public IntPtr EmulatorWindow => _emulator;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = Win32.CreateWindowEx(0, Win32.BlackContainerClass, "", (int)(Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN),
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _container);
    }

    public void Embed(IntPtr emulator)
    {
        _emulator = emulator;
        long style = Win32.GetStyle(emulator, Win32.GWL_STYLE);
        style &= ~(Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX);
        style |= Win32.WS_CHILD | Win32.WS_VISIBLE;
        Win32.SetStyle(emulator, Win32.GWL_STYLE, style);
        long ex = Win32.GetStyle(emulator, Win32.GWL_EXSTYLE);
        Win32.SetStyle(emulator, Win32.GWL_EXSTYLE, ex & ~Win32.WS_EX_APPWINDOW);
        Win32.SetParent(emulator, _container);
        Win32.SetWindowPos(emulator, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
        Win32.ShowWindow(emulator, Win32.SW_SHOW);
        RequestResize();
    }

    /// <summary>
    /// Sizes the emulator window to the container. The emulator rescales its display about
    /// half a second later (only once its first frame exists, hence <see cref="Nudge"/>).
    /// </summary>
    public void RequestResize()
    {
        if (!IsAlive) return;
        Win32.GetClientRect(_container, out var target);
        Win32.MoveWindow(_emulator, 0, 0, target.Width, target.Height, true);
    }

    /// <summary>Forces a fresh resize, for when an earlier one arrived before the emulator could scale.</summary>
    public void Nudge()
    {
        if (!IsAlive) return;
        Win32.GetClientRect(_container, out var target);
        Win32.MoveWindow(_emulator, 0, 0, Math.Max(1, target.Width - 2), Math.Max(1, target.Height - 2), false);
        Win32.MoveWindow(_emulator, 0, 0, target.Width, target.Height, true);
    }

    /// <summary>
    /// Keeps the emulator window centred in the container. Only asks for a resize when the
    /// size is clearly wrong (e.g. after rotation), never for the emulator's own aspect snapping.
    /// </summary>
    public void KeepFitted()
    {
        if (!IsAlive) return;
        Win32.GetClientRect(_container, out var target);
        Win32.GetWindowRect(_emulator, out var current);
        Win32.GetWindowRect(_container, out var origin);
        int dw = target.Width - current.Width, dh = target.Height - current.Height;
        bool fitsOneSide = Math.Abs(dw) <= SizeTolerance || Math.Abs(dh) <= SizeTolerance;
        if (!fitsOneSide || dw < -SizeTolerance || dh < -SizeTolerance)
        {
            RequestResize();
            return;
        }
        int x = Math.Max(0, dw / 2), y = Math.Max(0, dh / 2);
        if (current.Left - origin.Left != x || current.Top - origin.Top != y)
            Win32.SetWindowPos(_emulator, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    public void FocusEmulator()
    {
        if (_emulator != IntPtr.Zero) Win32.SetFocus(_emulator);
    }

    private bool IsAlive => _emulator != IntPtr.Zero && Win32.IsWindow(_emulator);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RequestResize();
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Give the emulator window back before our container goes away, otherwise Windows
        // would destroy it along with its new parent.
        if (IsAlive)
        {
            Win32.ShowWindow(_emulator, Win32.SW_HIDE);
            Win32.SetParent(_emulator, IntPtr.Zero);
        }
        Win32.DestroyWindow(hwnd.Handle);
    }
}
