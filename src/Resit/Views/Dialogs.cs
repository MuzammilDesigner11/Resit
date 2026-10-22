using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Resit.Views;

/// <summary>Small message and confirmation dialogs in Resit's style (Avalonia has no MessageBox).</summary>
public static class Dialogs
{
    /// <summary>Asks a question; true when the user picked <paramref name="confirm"/>.</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string confirm = "OK", string? copyText = null) =>
        Build(title, message, confirm, cancel: "Cancel", copyText).ShowDialog<bool>(owner);

    public static Task ShowAsync(Window owner, string title, string message, string? copyText = null) =>
        Build(title, message, "OK", cancel: null, copyText).ShowDialog<bool>(owner);

    private static Window Build(string title, string message, string confirm, string? cancel, string? copyText)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("PanelBackground"),
        };

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brush("TextPrimary") });
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondary") });

        if (copyText is not null)
        {
            // A command the user has to run themselves: selectable, with a copy button.
            var box = new TextBox { Text = copyText, IsReadOnly = true, FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap };
            var copy = new Button { Content = "Copy", Theme = Theme("SubtleButton"), Padding = new Thickness(12, 6), Margin = new Thickness(8, 0, 0, 0) };
            copy.Click += async (_, _) =>
            {
                if (window.Clipboard is { } clipboard) await clipboard.SetTextAsync(copyText);
                copy.Content = "Copied";
            };
            var row = new DockPanel();
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            row.Children.Add(box);
            body.Children.Add(row);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Spacing = 10 };
        if (cancel is not null)
        {
            var no = new Button { Content = cancel, Theme = Theme("SubtleButton"), IsCancel = true };
            no.Click += (_, _) => window.Close(false);
            buttons.Children.Add(no);
        }
        var yes = new Button { Content = confirm, Theme = Theme("AccentButton"), IsDefault = true };
        yes.Click += (_, _) => window.Close(true);
        buttons.Children.Add(yes);
        body.Children.Add(buttons);

        window.Content = body;
        return window;
    }

    private static IBrush? Brush(string key) =>
        Application.Current!.TryFindResource(key, out var value) ? value as IBrush : null;

    private static Avalonia.Styling.ControlTheme? Theme(string key) =>
        Application.Current!.TryFindResource(key, out var value) ? value as Avalonia.Styling.ControlTheme : null;
}
