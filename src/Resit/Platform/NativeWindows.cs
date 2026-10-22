using System.Runtime.Versioning;
using Avalonia;
using Resit.Core;

namespace Resit.Platform;

/// <summary>
/// The native-window work behind embedding: finding the emulator's windows, re-parenting its
/// screen into Resit, and the small window tricks the player and its overlays need. Sizes and
/// positions are in physical pixels.
/// </summary>
public abstract class NativeWindows
{
    public static NativeWindows Current { get; } =
        OperatingSystem.IsWindows() ? new Win32Windows() :
        OperatingSystem.IsLinux() ? new X11Windows() :
        throw new PlatformNotSupportedException("Resit runs on Windows and Linux.");

    /// <summary>What Avalonia calls this OS's window handles ("HWND", "XID").</summary>
    public abstract string HandleDescriptor { get; }

    /// <summary>The emulator's main (screen) window, or zero while it has not appeared yet.</summary>
    public abstract IntPtr FindEmulatorWindow(int processId);

    /// <summary>Hides the emulator's own floating toolbar; Resit's sidebar replaces it.</summary>
    public abstract void HideEmulatorToolbars(int processId);

    /// <summary>A plain black child window of <paramref name="parent"/> to hold the emulator.</summary>
    public abstract IntPtr CreateContainer(IntPtr parent);

    /// <summary>Gives the emulator window back (if still inside) and destroys the container.</summary>
    public abstract void DestroyContainer(IntPtr container, IntPtr embedded);

    /// <summary>Turns the emulator's top-level window into a borderless child of the container.</summary>
    public abstract void Embed(IntPtr container, IntPtr window);

    public abstract bool IsAlive(IntPtr window);

    /// <summary>Moves and sizes a child window inside its parent.</summary>
    public abstract void SetBounds(IntPtr window, PixelRect bounds);

    /// <summary>A child window's position and size inside its parent.</summary>
    public abstract PixelRect GetBounds(IntPtr window);

    /// <summary>A window's position and size on screen.</summary>
    public abstract PixelRect GetScreenBounds(IntPtr window);

    public abstract void Focus(IntPtr window);

    public abstract PixelPoint CursorPosition();

    /// <summary>Lets clicks pass through a top-level window to whatever is under it.</summary>
    public abstract void SetClickThrough(IntPtr topLevel, bool on);

    /// <summary>Keeps a top-level window (an overlay) from taking focus when clicked, or lets it again.</summary>
    public abstract void SetNoActivate(IntPtr topLevel, bool on);

    /// <summary>Whether this top-level window is the one the user is working in.</summary>
    public abstract bool IsForeground(IntPtr topLevel);

    /// <summary>
    /// Whether click-through overlays work here. On Linux the window manager may wrap an
    /// overlay in a frame that still catches the clicks, which would block the game, so the
    /// key labels are only drawn while editing there.
    /// </summary>
    public abstract bool SupportsClickThroughOverlay { get; }
}

[SupportedOSPlatform("windows")]
internal sealed class Win32Windows : NativeWindows
{
    public override string HandleDescriptor => "HWND";

    /// <summary>
    /// The main window's title varies between versions ("Emulator", "Android Emulator -
    /// name:port"), so it is found by its Qt window class instead.
    /// </summary>
    public override IntPtr FindEmulatorWindow(int processId) =>
        Win32.TopLevelWindowsOf(processId)
            .Where(h => Win32.IsWindowVisible(h) && Win32.GetClass(h).EndsWith("QWindowIcon"))
            .FirstOrDefault(h => !Win32.GetText(h).Contains("Extended", StringComparison.OrdinalIgnoreCase));

    public override void HideEmulatorToolbars(int processId)
    {
        foreach (var w in Win32.TopLevelWindowsOf(processId).Where(h => Win32.GetClass(h).Contains("QWindowTool")))
            if (Win32.IsWindowVisible(w)) Win32.ShowWindow(w, Win32.SW_HIDE);
    }

    public override IntPtr CreateContainer(IntPtr parent) =>
        Win32.CreateWindowEx(0, Win32.BlackContainerClass, "", (int)(Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN),
            0, 0, 1, 1, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    public override void DestroyContainer(IntPtr container, IntPtr embedded)
    {
        // Give the emulator window back before the container goes away, otherwise Windows
        // destroys it along with its new parent.
        if (embedded != IntPtr.Zero && Win32.IsWindow(embedded))
        {
            Win32.ShowWindow(embedded, Win32.SW_HIDE);
            Win32.SetParent(embedded, IntPtr.Zero);
        }
        Win32.DestroyWindow(container);
    }

    public override void Embed(IntPtr container, IntPtr window)
    {
        long style = Win32.GetStyle(window, Win32.GWL_STYLE);
        style &= ~(Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX);
        style |= Win32.WS_CHILD | Win32.WS_VISIBLE;
        Win32.SetStyle(window, Win32.GWL_STYLE, style);
        long ex = Win32.GetStyle(window, Win32.GWL_EXSTYLE);
        Win32.SetStyle(window, Win32.GWL_EXSTYLE, ex & ~Win32.WS_EX_APPWINDOW);
        Win32.SetParent(window, container);
        Win32.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
        Win32.ShowWindow(window, Win32.SW_SHOW);
    }

    public override bool IsAlive(IntPtr window) => window != IntPtr.Zero && Win32.IsWindow(window);

    public override void SetBounds(IntPtr window, PixelRect b) =>
        Win32.SetWindowPos(window, IntPtr.Zero, b.X, b.Y, Math.Max(1, b.Width), Math.Max(1, b.Height),
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

    public override PixelRect GetBounds(IntPtr window)
    {
        Win32.GetWindowRect(window, out var r);
        var parent = Win32.GetAncestor(window, 1 /* GA_PARENT */);
        Win32.GetWindowRect(parent, out var p);
        return new PixelRect(r.Left - p.Left, r.Top - p.Top, r.Width, r.Height);
    }

    public override PixelRect GetScreenBounds(IntPtr window)
    {
        Win32.GetWindowRect(window, out var r);
        return new PixelRect(r.Left, r.Top, r.Width, r.Height);
    }

    public override void Focus(IntPtr window)
    {
        if (window != IntPtr.Zero) Win32.SetFocus(window);
    }

    public override PixelPoint CursorPosition()
    {
        Win32.GetCursorPos(out var p);
        return new PixelPoint(p.X, p.Y);
    }

    public override void SetClickThrough(IntPtr topLevel, bool on) => Win32.SetClickThrough(topLevel, on);

    public override void SetNoActivate(IntPtr topLevel, bool on)
    {
        long ex = Win32.GetStyle(topLevel, Win32.GWL_EXSTYLE) | Win32.WS_EX_TOOLWINDOW;
        Win32.SetStyle(topLevel, Win32.GWL_EXSTYLE, on ? ex | Win32.WS_EX_NOACTIVATE : ex & ~Win32.WS_EX_NOACTIVATE);
    }

    public override bool IsForeground(IntPtr topLevel) =>
        topLevel != IntPtr.Zero && Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOT) == topLevel;

    public override bool SupportsClickThroughOverlay => true;
}

/// <summary>
/// X11 version. The emulator's Qt window is a normal top-level window managed (framed) by the
/// window manager, so embedding first withdraws it from the window manager, then re-parents it.
/// All calls go through one display connection owned by the UI thread.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11Windows : NativeWindows
{
    private readonly Lazy<IntPtr> _display = new(() =>
    {
        var d = X11.XOpenDisplay(IntPtr.Zero);
        if (d == IntPtr.Zero) throw new InvalidOperationException("Cannot connect to the X server (is DISPLAY set?).");
        return d;
    });

    private IntPtr Display => _display.Value;
    private IntPtr Root => X11.XDefaultRootWindow(Display);

    public override string HandleDescriptor => "XID";

    /// <summary>Every window the window manager manages, falling back to walking the tree.</summary>
    private List<IntPtr> ClientWindows()
    {
        var list = X11.GetLongs(Display, Root, "_NET_CLIENT_LIST").Select(v => new IntPtr(v)).ToList();
        if (list.Count > 0) return list;
        // No EWMH window manager: top-level windows and the ones inside frames.
        var result = new List<IntPtr>();
        foreach (var w in X11.ChildrenOf(Display, Root))
        {
            result.Add(w);
            result.AddRange(X11.ChildrenOf(Display, w));
        }
        return result;
    }

    private IEnumerable<IntPtr> WindowsOf(int processId) =>
        ClientWindows().Where(w => X11.GetLongs(Display, w, "_NET_WM_PID") is [var pid, ..] && pid == processId);

    private string WindowType(IntPtr window)
    {
        var types = X11.GetLongs(Display, window, "_NET_WM_WINDOW_TYPE");
        if (types.Count == 0) return "";
        foreach (var name in new[] { "_NET_WM_WINDOW_TYPE_UTILITY", "_NET_WM_WINDOW_TYPE_TOOLBAR", "_NET_WM_WINDOW_TYPE_DIALOG", "_NET_WM_WINDOW_TYPE_NORMAL" })
        {
            var atom = X11.XInternAtom(Display, name, true);
            if (atom != IntPtr.Zero && types.Contains(atom.ToInt64())) return name["_NET_WM_WINDOW_TYPE_".Length..];
        }
        return "OTHER";
    }

    private string Title(IntPtr window) =>
        X11.GetString(Display, window, "_NET_WM_NAME") ?? X11.GetString(Display, window, "WM_NAME") ?? "";

    private bool IsViewable(IntPtr window) =>
        X11.XGetWindowAttributes(Display, window, out var a) != 0 && a.map_state == X11.IsViewable;

    private (int W, int H) SizeOf(IntPtr window) =>
        X11.XGetGeometry(Display, window, out _, out _, out _, out uint w, out uint h, out _, out _) != 0 ? ((int)w, (int)h) : (0, 0);

    public override IntPtr FindEmulatorWindow(int processId)
    {
        var candidates = WindowsOf(processId)
            .Where(w => WindowType(w) is "" or "NORMAL" && IsViewable(w))
            .Where(w => !Title(w).Contains("Extended", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count > 1)
            Log.Write($"x11: {candidates.Count} emulator windows: {string.Join(", ", candidates.Select(w => $"0x{w:x} '{Title(w)}'"))}");
        // The screen window is the big one.
        return candidates.OrderByDescending(w => { var (width, height) = SizeOf(w); return (long)width * height; }).FirstOrDefault();
    }

    public override void HideEmulatorToolbars(int processId)
    {
        foreach (var w in WindowsOf(processId))
        {
            if (WindowType(w) is not ("UTILITY" or "TOOLBAR") || !IsViewable(w)) continue;
            if (Title(w).Contains("Extended", StringComparison.OrdinalIgnoreCase)) continue;
            X11.XUnmapWindow(Display, w);
        }
        X11.XFlush(Display);
    }

    public override IntPtr CreateContainer(IntPtr parent)
    {
        var w = X11.XCreateSimpleWindow(Display, parent, 0, 0, 1, 1, 0, UIntPtr.Zero, UIntPtr.Zero);
        X11.XMapWindow(Display, w);
        // Avalonia uses the window from its own connection right away.
        X11.XSync(Display, false);
        return w;
    }

    public override void DestroyContainer(IntPtr container, IntPtr embedded)
    {
        // X destroys every window inside the container, including the emulator's, which would
        // take the emulator down without saving its snapshot. Hand it back to the root first.
        if (embedded != IntPtr.Zero && IsAlive(embedded) && X11.ParentOf(Display, embedded) == container)
        {
            X11.XUnmapWindow(Display, embedded);
            X11.XReparentWindow(Display, embedded, Root, 0, 0);
        }
        X11.XDestroyWindow(Display, container);
        X11.XSync(Display, false);
    }

    public override void Embed(IntPtr container, IntPtr window)
    {
        // Withdraw it first: the window manager then takes its frame away and puts the window
        // back on the root. Re-parenting before that has happened lets the window manager
        // "restore" it out of the container again.
        X11.XUnmapWindow(Display, window);
        X11.XSync(Display, false);
        var root = Root;
        for (int i = 0; i < 50 && X11.ParentOf(Display, window) != root; i++)
        {
            Thread.Sleep(10);
            X11.XSync(Display, false);
        }
        X11.XReparentWindow(Display, window, container, 0, 0);
        X11.XMapWindow(Display, window);
        X11.XSync(Display, false);
        Log.Write($"x11: embedded 0x{window:x} in 0x{container:x} (parent now 0x{X11.ParentOf(Display, window):x})");
    }

    public override bool IsAlive(IntPtr window) =>
        window != IntPtr.Zero && X11.XGetWindowAttributes(Display, window, out _) != 0;

    public override void SetBounds(IntPtr window, PixelRect b)
    {
        X11.XMoveResizeWindow(Display, window, b.X, b.Y, (uint)Math.Max(1, b.Width), (uint)Math.Max(1, b.Height));
        X11.XFlush(Display);
    }

    public override PixelRect GetBounds(IntPtr window) =>
        X11.XGetGeometry(Display, window, out _, out int x, out int y, out uint w, out uint h, out _, out _) != 0
            ? new PixelRect(x, y, (int)w, (int)h)
            : default;

    public override PixelRect GetScreenBounds(IntPtr window)
    {
        var size = GetBounds(window);
        X11.XTranslateCoordinates(Display, window, Root, 0, 0, out int x, out int y, out _);
        return new PixelRect(x, y, size.Width, size.Height);
    }

    public override void Focus(IntPtr window)
    {
        // Key presses are grabbed at the player window (see X11KeyCapture), so focus inside it
        // only decides who sees the mouse wheel and text-input state; failures are harmless.
        if (window == IntPtr.Zero || !IsViewable(window)) return;
        X11.XSetInputFocus(Display, window, X11.RevertToParent, IntPtr.Zero);
        X11.XFlush(Display);
    }

    public override PixelPoint CursorPosition()
    {
        X11.XQueryPointer(Display, Root, out _, out _, out int x, out int y, out _, out _, out _);
        return new PixelPoint(x, y);
    }

    public override void SetClickThrough(IntPtr topLevel, bool on)
    {
        if (on) X11.XShapeCombineRectangles(Display, topLevel, X11.ShapeInput, 0, 0, IntPtr.Zero, 0, X11.ShapeSet, 0);
        else X11.XShapeCombineMask(Display, topLevel, X11.ShapeInput, 0, 0, IntPtr.Zero, X11.ShapeSet);
        X11.XFlush(Display);
    }

    public override void SetNoActivate(IntPtr topLevel, bool on)
    {
        // Avalonia's ShowActivated = false is all X11 needs.
    }

    public override bool IsForeground(IntPtr topLevel)
    {
        // Without an EWMH window manager there is no way to tell; the key grab only fires while
        // focus is inside the player anyway.
        var active = X11.GetLongs(Display, Root, "_NET_ACTIVE_WINDOW");
        return active is not [var w, ..] || w == topLevel.ToInt64();
    }

    public override bool SupportsClickThroughOverlay => false;
}
