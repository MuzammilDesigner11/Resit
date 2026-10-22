using System.IO.Pipes;
using Resit.Core;
using Resit.Platform;

namespace Resit;

/// <summary>
/// Keeps one Resit per user. The first copy holds a named mutex and listens on a named pipe;
/// a second copy finds the mutex taken, pokes the pipe so the first one shows itself, and quits.
/// Both work the same on Windows and Linux.
/// </summary>
internal static class SingleInstance
{
    private static readonly string PipeName = $"Resit.Activate.{Environment.UserName}";
    private static Mutex? _mutex;

    /// <summary>True for the first copy. Otherwise the running copy has been asked to come forward.</summary>
    public static bool Claim()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\Resit.SingleInstance", out bool first);
        if (first) return true;

        // Only the launched process may hand over the foreground on Windows.
        if (OperatingSystem.IsWindows()) Win32.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1000);
            pipe.WriteByte(1);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            Log.Write($"could not reach the running Resit: {ex.Message}");
        }
        return false;
    }

    /// <summary>Calls <paramref name="activate"/> (on a background thread) whenever another copy starts.</summary>
    public static void Listen(Action activate)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    server.ReadByte();
                    activate();
                }
                catch (IOException ex)
                {
                    Log.Write($"activation pipe: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }) { IsBackground = true, Name = "Resit activation" };
        thread.Start();
    }
}
