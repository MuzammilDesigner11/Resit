using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Resit.Core;
using Resit.Native;

namespace Resit.Views;

/// <summary>
/// Transparent window over the emulator screen. While playing it shows key labels and passes
/// every click through to the game; while editing it lets the user place and bind controls.
/// </summary>
public partial class KeymapOverlay : Window
{
    private const double TapSize = 46;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private IntPtr _hwnd;
    private KeymapProfile _editing = new();
    private KeyControl? _selected;
    private KeyControl? _dragging;
    private Point _dragOffset;

    public KeymapProfile Profile { get; private set; } = new();
    public string Package { get; private set; } = KeymapStore.Default;
    public bool IsEditing { get; private set; }

    private bool _showHints = true;
    public bool ShowHints
    {
        get => _showHints;
        set
        {
            _showHints = value;
            Render();
        }
    }

    /// <summary>Raised with the new profile when the user saves the layout.</summary>
    public event Action<KeymapProfile>? Saved;
    public event Action? EditingEnded;

    public KeymapOverlay(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Win32.GetStyle(_hwnd, Win32.GWL_EXSTYLE);
            Win32.SetStyle(_hwnd, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW);
            SetClickThrough(true);
        };
        SizeChanged += (_, _) => Render();
        KeyDown += OnKeyDown;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
    }

    public void SetProfile(KeymapProfile profile, string package)
    {
        Profile = profile;
        Package = package;
        if (!IsEditing) Render();
    }

    public void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void BeginEdit()
    {
        if (IsEditing) return;
        IsEditing = true;
        _editing = Clone(Profile);
        _selected = null;
        SetClickThrough(false);
        EditTint.Visibility = Visibility.Visible;
        EditBar.Visibility = Visibility.Visible;
        UpdateHint();
        Render();
        Activate();
        Focus();
    }

    public void EndEdit()
    {
        if (!IsEditing) return;
        IsEditing = false;
        _selected = null;
        _dragging = null;
        EditTint.Visibility = Visibility.Collapsed;
        EditBar.Visibility = Visibility.Collapsed;
        SetClickThrough(true);
        Render();
        EditingEnded?.Invoke();
    }

    private void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = Win32.GetStyle(_hwnd, Win32.GWL_EXSTYLE);
        ex = on ? ex | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE : ex & ~(Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE);
        Win32.SetStyle(_hwnd, Win32.GWL_EXSTYLE, ex);
    }

    // ---- Editor commands -------------------------------------------------------------

    private void OnAddTap(object sender, RoutedEventArgs e)
    {
        var tap = new TapControl { X = 0.5, Y = 0.5 };
        _editing.Controls.Add(tap);
        _selected = tap;
        UpdateHint();
        Render();
    }

    private void OnAddJoystick(object sender, RoutedEventArgs e)
    {
        var stick = new JoystickControl { X = 0.18, Y = 0.72 };
        _editing.Controls.Add(stick);
        _selected = stick;
        UpdateHint();
        Render();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _editing.Controls.Clear();
        _selected = null;
        UpdateHint();
        Render();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => EndEdit();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _editing.Controls.RemoveAll(c => c is TapControl { Key: Key.None });
        Profile = _editing;
        KeymapStore.Save(Package, Profile);
        Saved?.Invoke(Profile);
        EndEdit();
        ShowToast(Package == KeymapStore.Default ? "Controls saved" : $"Controls saved for {Package}");
    }

    private void OnTintClick(object sender, MouseButtonEventArgs e)
    {
        _selected = null;
        UpdateHint();
        Render();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEditing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        switch (_selected)
        {
            case null when key == Key.Escape:
                EndEdit();
                return;
            case null:
                return;
            case not null when key == Key.Escape:
                _selected = null;
                break;
            case not null when key is Key.Delete:
                _editing.Controls.Remove(_selected);
                _selected = null;
                break;
            case TapControl tap:
                tap.Key = key;
                break;
            case JoystickControl stick when key is Key.Up or Key.Down or Key.Left or Key.Right:
                (stick.Up, stick.Left, stick.Down, stick.Right) = (Key.Up, Key.Left, Key.Down, Key.Right);
                break;
            case JoystickControl stick when key is Key.W or Key.A or Key.S or Key.D:
                (stick.Up, stick.Left, stick.Down, stick.Right) = (Key.W, Key.A, Key.S, Key.D);
                break;
        }
        UpdateHint();
        Render();
    }

    private void UpdateHint()
    {
        EditHint.Text = _selected switch
        {
            TapControl { Key: Key.None } => "Press the key for this button. Drag to move it. Delete removes it.",
            TapControl => "Press another key to rebind. Drag to move. Delete removes it.",
            JoystickControl => "Drag to move. Scroll to resize. Press W or ↑ to use WASD or the arrow keys. Delete removes it.",
            _ => "Add a Tap button or a Joystick, then drag it over the game's on-screen control. Save when done.",
        };
    }

    // ---- Drawing ---------------------------------------------------------------------

    private void Render()
    {
        ControlsCanvas.Children.Clear();
        if (ActualWidth < 1 || ActualHeight < 1) return;
        if (!IsEditing && !ShowHints) return;

        foreach (var control in (IsEditing ? _editing : Profile).Controls)
        {
            FrameworkElement visual = control switch
            {
                TapControl tap => TapVisual(tap),
                JoystickControl stick => JoystickVisual(stick),
                _ => throw new NotSupportedException(),
            };
            Canvas.SetLeft(visual, control.X * ActualWidth - visual.Width / 2);
            Canvas.SetTop(visual, control.Y * ActualHeight - visual.Height / 2);
            if (IsEditing)
            {
                visual.Cursor = Cursors.SizeAll;
                visual.Tag = control;
                visual.MouseLeftButtonDown += OnControlDown;
                visual.MouseMove += OnControlMove;
                visual.MouseLeftButtonUp += OnControlUp;
                visual.MouseRightButtonUp += (_, _) => { _editing.Controls.Remove(control); _selected = null; UpdateHint(); Render(); };
                if (control is JoystickControl j) visual.MouseWheel += (_, e) => ResizeJoystick(j, e.Delta);
            }
            else
            {
                visual.Opacity = 0.6;
                visual.IsHitTestVisible = false;
            }
            ControlsCanvas.Children.Add(visual);
        }
    }

    private Brush Outline(KeyControl c) =>
        ReferenceEquals(c, _selected) ? (Brush)FindResource("Highlight") : new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    private FrameworkElement TapVisual(TapControl tap)
    {
        var grid = new Grid { Width = TapSize, Height = TapSize };
        grid.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x11, 0x16)),
            Stroke = Outline(tap),
            StrokeThickness = ReferenceEquals(tap, _selected) ? 3 : 1.5,
        });
        grid.Children.Add(Label(tap.Key == Key.None ? "?" : KeyLabel(tap.Key), 14));
        return grid;
    }

    private FrameworkElement JoystickVisual(JoystickControl stick)
    {
        double d = Math.Max(60, 2 * stick.Radius * Math.Min(ActualWidth, ActualHeight) + TapSize);
        var grid = new Grid { Width = d, Height = d };
        grid.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x10, 0x11, 0x16)),
            Stroke = Outline(stick),
            StrokeThickness = ReferenceEquals(stick, _selected) ? 3 : 1.5,
            StrokeDashArray = [4, 3],
        });
        grid.Children.Add(new Ellipse
        {
            Width = 14, Height = 14,
            Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
        });
        void Add(Key key, HorizontalAlignment h, VerticalAlignment v)
        {
            var label = Label(KeyLabel(key), 13);
            label.HorizontalAlignment = h;
            label.VerticalAlignment = v;
            label.Margin = new Thickness(8);
            grid.Children.Add(label);
        }
        Add(stick.Up, HorizontalAlignment.Center, VerticalAlignment.Top);
        Add(stick.Down, HorizontalAlignment.Center, VerticalAlignment.Bottom);
        Add(stick.Left, HorizontalAlignment.Left, VerticalAlignment.Center);
        Add(stick.Right, HorizontalAlignment.Right, VerticalAlignment.Center);
        return grid;
    }

    private static TextBlock Label(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static string KeyLabel(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.Space => "Space",
        Key.Return => "Enter",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.Tab => "Tab",
        Key.Back => "Bksp",
        Key.Capital => "Caps",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemTilde => "`",
        Key.OemPipe => "\\",
        _ => key.ToString(),
    };

    // ---- Dragging --------------------------------------------------------------------

    private void OnControlDown(object sender, MouseButtonEventArgs e)
    {
        var visual = (FrameworkElement)sender;
        _dragging = (KeyControl)visual.Tag;
        _selected = _dragging;
        var p = e.GetPosition(ControlsCanvas);
        _dragOffset = new Point(p.X - _dragging.X * ActualWidth, p.Y - _dragging.Y * ActualHeight);
        visual.CaptureMouse();
        UpdateHint();
        e.Handled = true;
    }

    private void OnControlMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed) return;
        var visual = (FrameworkElement)sender;
        var p = e.GetPosition(ControlsCanvas);
        _dragging.X = Math.Clamp((p.X - _dragOffset.X) / ActualWidth, 0, 1);
        _dragging.Y = Math.Clamp((p.Y - _dragOffset.Y) / ActualHeight, 0, 1);
        Canvas.SetLeft(visual, _dragging.X * ActualWidth - visual.Width / 2);
        Canvas.SetTop(visual, _dragging.Y * ActualHeight - visual.Height / 2);
    }

    private void OnControlUp(object sender, MouseButtonEventArgs e)
    {
        ((FrameworkElement)sender).ReleaseMouseCapture();
        _dragging = null;
        Render();   // redraw with the selection outline
    }

    private void ResizeJoystick(JoystickControl stick, int delta)
    {
        stick.Radius = Math.Clamp(stick.Radius + delta / 120.0 * 0.01, 0.03, 0.3);
        _selected = stick;
        Render();
    }

    private static KeymapProfile Clone(KeymapProfile p) => new()
    {
        Controls = p.Controls.Select(c => c switch
        {
            TapControl t => (KeyControl)new TapControl { X = t.X, Y = t.Y, Key = t.Key },
            JoystickControl j => new JoystickControl { X = j.X, Y = j.Y, Up = j.Up, Down = j.Down, Left = j.Left, Right = j.Right, Radius = j.Radius },
            _ => throw new NotSupportedException(),
        }).ToList(),
    };
}
