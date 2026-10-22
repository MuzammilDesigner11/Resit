using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Resit.Platform;

namespace Resit.Views;

/// <summary>
/// Hosts the emulator's own window inside Resit. The emulator keeps rendering straight to its
/// window with the GPU, so embedding costs no performance (unlike streaming frames).
/// </summary>
internal sealed class EmulatorHost : NativeControlHost
{
    // The emulator snaps its window to the display's aspect ratio after a resize, so a few
    // pixels of difference from our container are expected and must not trigger a new resize
    // (that would loop forever).
    private const int SizeTolerance = 6;

    private static NativeWindows Native => NativeWindows.Current;

    private IntPtr _container;
    private IntPtr _emulator;

    public IntPtr EmulatorWindow => _emulator;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _container = Native.CreateContainer(parent.Handle);
        // Created again after the control was moved in the visual tree: take the emulator along.
        if (Native.IsAlive(_emulator)) Native.Embed(_container, _emulator);
        return new PlatformHandle(_container, Native.HandleDescriptor);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Native.DestroyContainer(control.Handle, _emulator);
        _container = IntPtr.Zero;
    }

    public void Embed(IntPtr emulator)
    {
        _emulator = emulator;
        // If the container does not exist yet, CreateNativeControlCore embeds it.
        if (_container != IntPtr.Zero) Native.Embed(_container, emulator);
        RequestResize();
    }

    /// <summary>The container's size in pixels (Avalonia sizes it to this control).</summary>
    private PixelSize Target
    {
        get
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return new PixelSize(Math.Max(1, (int)Math.Round(Bounds.Width * scale)), Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
        }
    }

    private bool IsAlive => _container != IntPtr.Zero && Native.IsAlive(_emulator);

    /// <summary>
    /// Sizes the emulator window to the container. The emulator rescales its display about
    /// half a second later (only once its first frame exists, hence <see cref="Nudge"/>).
    /// </summary>
    public void RequestResize()
    {
        if (!IsAlive) return;
        var t = Target;
        Native.SetBounds(_emulator, new PixelRect(0, 0, t.Width, t.Height));
    }

    /// <summary>Forces a fresh resize, for when an earlier one arrived before the emulator could scale.</summary>
    public void Nudge()
    {
        if (!IsAlive) return;
        var t = Target;
        Native.SetBounds(_emulator, new PixelRect(0, 0, Math.Max(1, t.Width - 2), Math.Max(1, t.Height - 2)));
        Native.SetBounds(_emulator, new PixelRect(0, 0, t.Width, t.Height));
    }

    /// <summary>
    /// Keeps the emulator window centred in the container. Only asks for a resize when the
    /// size is clearly wrong (e.g. after rotation), never for the emulator's own aspect snapping.
    /// </summary>
    public void KeepFitted()
    {
        if (!IsAlive) return;
        var target = Target;
        var current = Native.GetBounds(_emulator);
        int dw = target.Width - current.Width, dh = target.Height - current.Height;
        bool fitsOneSide = Math.Abs(dw) <= SizeTolerance || Math.Abs(dh) <= SizeTolerance;
        if (!fitsOneSide || dw < -SizeTolerance || dh < -SizeTolerance)
        {
            RequestResize();
            return;
        }
        int x = Math.Max(0, dw / 2), y = Math.Max(0, dh / 2);
        if (current.X != x || current.Y != y)
            Native.SetBounds(_emulator, new PixelRect(x, y, current.Width, current.Height));
    }

    public void FocusEmulator()
    {
        if (IsAlive) Native.Focus(_emulator);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RequestResize();
    }
}
