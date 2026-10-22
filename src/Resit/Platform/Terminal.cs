using System.Diagnostics;
using Resit.Core;

namespace Resit.Platform;

/// <summary>Runs a setup script in a terminal window the user can watch (and answer prompts in).</summary>
public static class Terminal
{
    /// <summary>False when no terminal could be started (Linux without a known terminal app).</summary>
    public static bool RunScript(string script)
    {
        var dir = Path.GetDirectoryName(script)!;
        if (OS.IsWindows)
        {
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", script },
                WorkingDirectory = dir,
                UseShellExecute = true,
            });
            return true;
        }

        // Keep the window open afterwards so the user can read the result.
        var command = $"bash '{script.Replace("'", "'\\''")}'; echo; read -rp 'Press Enter to close' _";
        (string Exe, string[] Args)[] terminals =
        [
            ("x-terminal-emulator", ["-e", "bash", "-c", command]),
            ("gnome-terminal", ["--", "bash", "-c", command]),
            ("konsole", ["-e", "bash", "-c", command]),
            ("xfce4-terminal", ["-x", "bash", "-c", command]),
            ("kgx", ["--", "bash", "-c", command]),
            ("alacritty", ["-e", "bash", "-c", command]),
            ("kitty", ["bash", "-c", command]),
            ("xterm", ["-e", "bash", "-c", command]),
        ];
        foreach (var (exe, args) in terminals)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed; try the next one.
            }
        }
        Log.Write("no terminal found to run setup");
        return false;
    }
}
