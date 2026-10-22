using System.Threading.Channels;
using Android.Emulation.Control;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace Resit.Core;

/// <summary>
/// Talks to a running emulator over its gRPC control API: keys, multi-touch, rotation,
/// screenshots and status.
/// </summary>
public sealed class EmulatorClient : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly EmulatorController.EmulatorControllerClient _controller;
    private readonly UiController.UiControllerClient _ui;
    private readonly Channel<InputEvent> _input = Channel.CreateUnbounded<InputEvent>(new() { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _inputPump;

    public EmulatorClient(int port, string? token)
    {
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
        CallInvoker invoker = _channel.CreateCallInvoker();
        if (!string.IsNullOrEmpty(token))
        {
            invoker = invoker.Intercept(metadata =>
            {
                metadata.Add("authorization", $"Bearer {token}");
                return metadata;
            });
        }
        _controller = new EmulatorController.EmulatorControllerClient(invoker);
        _ui = new UiController.UiControllerClient(invoker);
        _inputPump = Task.Run(PumpInputAsync);
    }

    public async Task<EmulatorStatus?> TryGetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _controller.getStatusAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            Log.Write($"getStatus failed: {ex.Status}");
            return null;
        }
    }

    /// <summary>Presses and releases a key by its W3C name, e.g. "GoBack", "GoHome", "AppSwitch", "AudioVolumeUp".</summary>
    public void PressKey(string key) => _input.Writer.TryWrite(new InputEvent
    {
        KeyEvent = new KeyboardEvent { Key = key, EventType = KeyboardEvent.Types.KeyEventType.Keypress },
    });

    /// <summary>
    /// Presses or releases a physical key, identified by its code on this OS (a Windows scan
    /// code, 0xE0xx for extended keys, or a Linux evdev code). The emulator maps it to the
    /// matching Android key, so held modifiers (Shift, Ctrl) work.
    /// </summary>
    public void SendKey(int code, bool down) => _input.Writer.TryWrite(new InputEvent
    {
        KeyEvent = new KeyboardEvent
        {
            CodeType = Platform.KeyCapture.CodeType,
            KeyCode = code,
            EventType = down ? KeyboardEvent.Types.KeyEventType.Keydown : KeyboardEvent.Types.KeyEventType.Keyup,
        },
    });

    /// <summary>Puts text on Android's clipboard (used to paste from the PC with Ctrl+V).</summary>
    public async Task SetClipboardAsync(string text)
    {
        try
        {
            await _controller.setClipboardAsync(new ClipData { Text = text }, deadline: DateTime.UtcNow.AddSeconds(3));
        }
        catch (RpcException ex)
        {
            Log.Write($"setClipboard failed: {ex.Status}");
        }
    }

    /// <summary>
    /// Moves or lifts a finger. Coordinates are in the device's native (unrotated) display pixels.
    /// Fingers never time out, so every down must be followed by an up.
    /// </summary>
    public void Touch(int id, int x, int y, bool down) => _input.Writer.TryWrite(new InputEvent
    {
        TouchEvent = new TouchEvent
        {
            Touches =
            {
                new Android.Emulation.Control.Touch
                {
                    Identifier = id,
                    X = x,
                    Y = y,
                    Pressure = down ? 1 : 0,
                    TouchMajor = down ? 8 : 0,
                    Expiration = Android.Emulation.Control.Touch.Types.EventExpiration.NeverExpire,
                },
            },
        },
    });

    /// <summary>Device rotation around the screen axis, in degrees (0, 90, 180 or -90).</summary>
    public async Task<double?> TryGetRotationAsync()
    {
        try
        {
            var v = await _controller.getPhysicalModelAsync(
                new PhysicalModelValue { Target = PhysicalModelValue.Types.PhysicalType.Rotation },
                deadline: DateTime.UtcNow.AddSeconds(2));
            return v.Value?.Data.Count >= 3 ? v.Value.Data[2] : null;
        }
        catch (RpcException ex)
        {
            Log.Write($"getPhysicalModel failed: {ex.Status}");
            return null;
        }
    }

    public async Task SetRotationAsync(double zDegrees)
    {
        var value = new ParameterValue();
        value.Data.Add([0f, 0f, (float)zDegrees]);
        await _controller.setPhysicalModelAsync(new PhysicalModelValue
        {
            Target = PhysicalModelValue.Types.PhysicalType.Rotation,
            Value = value,
            Interpolation = PhysicalModelValue.Types.Interpolation.Step,
        });
    }

    /// <summary>PNG of the screen as currently shown (already rotated).</summary>
    public async Task<byte[]> ScreenshotPngAsync()
    {
        var image = await _controller.getScreenshotAsync(new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png });
        return image.Image_.ToByteArray();
    }

    public async Task ShowExtendedControlsAsync()
    {
        try
        {
            await _ui.showExtendedControlsAsync(new PaneEntry { Index = PaneEntry.Types.PaneIndex.KeepCurrent });
        }
        catch (RpcException)
        {
            // Older emulators lack the UI service; nothing to show.
        }
    }

    private async Task PumpInputAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var call = _controller.streamInputEvent(cancellationToken: _cts.Token);
                await foreach (var ev in _input.Reader.ReadAllAsync(_cts.Token))
                    await call.RequestStream.WriteAsync(ev);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RpcException ex)
            {
                // Stream dropped (emulator restarting or still booting): reconnect shortly.
                Log.Write($"input stream failed: {ex.Status}");
                await Task.Delay(500).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Log.Write($"input stream failed: {ex.Message}");
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _input.Writer.TryComplete();
        _cts.Cancel();
        try { await _inputPump; } catch (OperationCanceledException) { }
        _channel.Dispose();
        _cts.Dispose();
    }
}
