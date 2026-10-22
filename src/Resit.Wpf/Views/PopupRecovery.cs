using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Resit.Native;

namespace Resit.Views;

/// <summary>
/// Works around a WPF bug: while an accessibility client is listening, Popup's MSAA-to-UIA
/// bridge can throw (an empty FileNotFoundException) after the popup window is built but
/// before it is shown, so dropdowns, menus and tooltips never appear. This shows those
/// stranded popups the way WPF would have.
/// </summary>
internal static class PopupRecovery
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo? ShowWindow = typeof(Popup).GetMethod("ShowWindow", Instance, Type.EmptyTypes);
    private static readonly MethodInfo? OnOpened = typeof(Popup).GetMethod("OnOpened", Instance, [typeof(EventArgs)]);

    /// <summary>Shows every popup that is open but whose window is still hidden. Returns how many.</summary>
    public static int ShowStranded()
    {
        if (ShowWindow is null) return 0;

        int shown = 0;
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().ToList())
        {
            if (source.IsDisposed || source.RootVisual?.GetType().Name != "PopupRoot") continue;
            if (Win32.IsWindowVisible(source.Handle)) continue;

            var popup = FindPopup(source.RootVisual);
            if (popup is null || !popup.IsOpen) continue;

            ShowWindow.Invoke(popup, null);
            OnOpened?.Invoke(popup, [EventArgs.Empty]);
            shown++;
        }
        return shown;
    }

    /// <summary>The popup's content sits inside its PopupRoot and has the Popup as logical parent.</summary>
    private static Popup? FindPopup(Visual root)
    {
        var pending = new Queue<DependencyObject>([root]);
        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            if (node is FrameworkElement { Parent: Popup popup }) return popup;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                pending.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }
}
