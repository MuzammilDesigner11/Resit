using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Resit.Platform;

/// <summary>
/// The parts of Xlib Resit uses on Linux. Avalonia runs on X11 there (XWayland on Wayland
/// desktops), and so does the emulator's Qt window, which is what makes embedding possible.
/// Resit opens its own display connections; window ids are shared by every client.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe class X11
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXext = "libXext.so.6";

    public const int KeyPress = 2, KeyRelease = 3;
    public const int IsViewable = 2;
    public const int GrabModeAsync = 1;
    public const uint AnyModifier = 1 << 15;
    public const int AnyKey = 0;
    public const int RevertToParent = 2;
    public const uint ShiftMask = 1, ControlMask = 4, Mod1Mask = 8, Mod4Mask = 64;
    public const int ShapeInput = 2, ShapeSet = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int x, y, width, height, border_width, depth;
        public IntPtr visual;
        public IntPtr root;
        public int c_class, bit_gravity, win_gravity, backing_store;
        public IntPtr backing_planes, backing_pixel;
        public int save_under;
        public IntPtr colormap;
        public int map_installed, map_state;
        public IntPtr all_event_masks, your_event_mask, do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }

    [DllImport(LibX11)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] public static extern int XConnectionNumber(IntPtr display);
    [DllImport(LibX11)] public static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] public static extern int XSync(IntPtr display, bool discard);
    [DllImport(LibX11)] public static extern int XPending(IntPtr display);
    [DllImport(LibX11)] public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(LibX11)] public static extern int XFree(IntPtr data);

    [DllImport(LibX11, CharSet = CharSet.Ansi)]
    public static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);

    [DllImport(LibX11)]
    public static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length,
        bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat, out IntPtr nItems, out IntPtr bytesAfter, out IntPtr prop);

    [DllImport(LibX11)]
    public static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);

    [DllImport(LibX11)] public static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);

    [DllImport(LibX11)]
    public static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y,
        out uint width, out uint height, out uint border, out uint depth);

    [DllImport(LibX11)]
    public static extern bool XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest, int x, int y, out int destX, out int destY, out IntPtr child);

    [DllImport(LibX11)]
    public static extern bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [DllImport(LibX11)]
    public static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, UIntPtr border, UIntPtr background);

    [DllImport(LibX11)] public static extern int XDestroyWindow(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern int XReparentWindow(IntPtr display, IntPtr window, IntPtr parent, int x, int y);
    [DllImport(LibX11)] public static extern int XMapWindow(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern int XUnmapWindow(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern int XMoveResizeWindow(IntPtr display, IntPtr window, int x, int y, uint width, uint height);
    [DllImport(LibX11)] public static extern int XMoveWindow(IntPtr display, IntPtr window, int x, int y);
    [DllImport(LibX11)] public static extern int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, IntPtr time);

    [DllImport(LibX11)]
    public static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode);

    [DllImport(LibX11)] public static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [DllImport(LibX11)] public static extern IntPtr XkbKeycodeToKeysym(IntPtr display, byte keycode, int group, int level);
    [DllImport(LibX11)] public static extern bool XkbSetDetectableAutoRepeat(IntPtr display, bool detectable, out bool supported);

    [DllImport(LibXext)]
    public static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int destKind, int xOff, int yOff,
        IntPtr rectangles, int count, int op, int ordering);

    [DllImport(LibXext)]
    public static extern void XShapeCombineMask(IntPtr display, IntPtr window, int destKind, int xOff, int yOff, IntPtr pixmap, int op);

    // ---- Helpers ----------------------------------------------------------------------

    /// <summary>A format-32 property as numbers (Xlib hands those back as C longs).</summary>
    public static List<long> GetLongs(IntPtr display, IntPtr window, string property)
    {
        var result = new List<long>();
        var atom = XInternAtom(display, property, true);
        if (atom == IntPtr.Zero) return result;
        if (XGetWindowProperty(display, window, atom, IntPtr.Zero, new IntPtr(4096), false, IntPtr.Zero,
                out _, out int format, out var count, out _, out var data) != 0 || data == IntPtr.Zero)
            return result;
        try
        {
            if (format == 32)
                for (long i = 0; i < count.ToInt64(); i++) result.Add(((nint*)data)[i]);
        }
        finally
        {
            XFree(data);
        }
        return result;
    }

    /// <summary>A text property such as _NET_WM_NAME or WM_NAME.</summary>
    public static string? GetString(IntPtr display, IntPtr window, string property)
    {
        var atom = XInternAtom(display, property, true);
        if (atom == IntPtr.Zero) return null;
        if (XGetWindowProperty(display, window, atom, IntPtr.Zero, new IntPtr(1024), false, IntPtr.Zero,
                out _, out int format, out var count, out _, out var data) != 0 || data == IntPtr.Zero)
            return null;
        try
        {
            return format == 8 ? Encoding.UTF8.GetString((byte*)data, (int)count.ToInt64()) : null;
        }
        finally
        {
            XFree(data);
        }
    }

    public static IntPtr ParentOf(IntPtr display, IntPtr window)
    {
        if (XQueryTree(display, window, out _, out var parent, out var children, out _) == 0) return IntPtr.Zero;
        if (children != IntPtr.Zero) XFree(children);
        return parent;
    }

    public static List<IntPtr> ChildrenOf(IntPtr display, IntPtr window)
    {
        var result = new List<IntPtr>();
        if (XQueryTree(display, window, out _, out _, out var children, out uint count) == 0) return result;
        if (children == IntPtr.Zero) return result;
        for (int i = 0; i < count; i++) result.Add(((IntPtr*)children)[i]);
        XFree(children);
        return result;
    }
}
