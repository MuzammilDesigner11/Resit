using System.Windows;
using System.Windows.Interop;
using Resit.Native;

namespace Resit.Views;

/// <summary>The bar that drops down from the top edge in fullscreen.</summary>
public partial class FullscreenBar : Window
{
    private readonly PlayerWindow _player;

    internal FullscreenBar(PlayerWindow player)
    {
        InitializeComponent();
        _player = player;
        Owner = player;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Never take focus from the game.
            Win32.SetStyle(hwnd, Win32.GWL_EXSTYLE, Win32.GetStyle(hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        };
    }

    private void OnBack(object sender, RoutedEventArgs e) => _player.PressDeviceKey("GoBack");
    private void OnHome(object sender, RoutedEventArgs e) => _player.PressDeviceKey("GoHome");
    private void OnRecents(object sender, RoutedEventArgs e) => _player.PressDeviceKey("AppSwitch");
    private void OnVolumeUp(object sender, RoutedEventArgs e) => _player.PressDeviceKey("AudioVolumeUp");
    private void OnVolumeDown(object sender, RoutedEventArgs e) => _player.PressDeviceKey("AudioVolumeDown");
    private void OnScreenshot(object sender, RoutedEventArgs e) => _player.TakeScreenshot();
    private void OnExit(object sender, RoutedEventArgs e) => _player.ToggleFullscreen();
}
