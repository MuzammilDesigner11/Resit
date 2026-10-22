using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Resit.Core;

namespace Resit.Platform;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public enum Flow { Output = 0, Input = 1 }

/// <summary>The PC's sound devices, and sending one program's sound (or microphone) to one of them.</summary>
public static class AudioDevices
{
    /// <summary>Active devices for one direction, by name.</summary>
    public static List<AudioDevice> List(Flow flow) =>
        OperatingSystem.IsWindows() ? WindowsAudio.List(flow) :
        OperatingSystem.IsLinux() ? PulseAudio.List(flow) : [];

    /// <summary>
    /// Sends a process's sound (or takes its microphone) to a device; null means "follow the
    /// system default". Returns false while that is not possible yet (the process has not
    /// opened its audio), so callers retry.
    /// </summary>
    public static bool TryRouteProcess(int processId, Flow flow, string? deviceId) =>
        OperatingSystem.IsWindows() ? WindowsAudio.TryRouteProcess(processId, flow, deviceId) :
        OperatingSystem.IsLinux() && PulseAudio.TryRouteProcess(processId, flow, deviceId);
}

/// <summary>
/// PulseAudio, or PipeWire through its PulseAudio interface (every mainstream desktop), driven
/// with pactl. The emulator plays through a normal stream tagged with its process id, which is
/// moved to the chosen device.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class PulseAudio
{
    private static bool _missingLogged;

    public static List<AudioDevice> List(Flow flow)
    {
        var output = Pactl("list", flow == Flow.Output ? "sinks" : "sources");
        var result = new List<AudioDevice>();
        foreach (var block in Blocks(output))
        {
            var name = Field(block, "Name");
            if (name is null || name.EndsWith(".monitor")) continue;   // a monitor records an output
            result.Add(new AudioDevice(name, Field(block, "Description") ?? name));
        }
        return result.OrderBy(d => d.Name).ToList();
    }

    public static bool TryRouteProcess(int processId, Flow flow, string? deviceId)
    {
        var kind = flow == Flow.Output ? "sink-input" : "source-output";
        var streams = Pactl("list", kind + "s");
        var mine = Blocks(streams)
            .Where(b => ProcessId().Match(b) is { Success: true } m && m.Groups[1].Value == processId.ToString())
            .Select(b => StreamIndex().Match(b))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();
        // No stream yet: with the default device there is nothing to do, a new stream goes there.
        if (mine.Count == 0) return deviceId is null;

        var target = deviceId ?? (flow == Flow.Output ? "@DEFAULT_SINK@" : "@DEFAULT_SOURCE@");
        foreach (var index in mine) Pactl("move-" + kind, index, target);
        return true;
    }

    /// <summary>"Sink #3", "Sink Input #12", … each followed by indented fields.</summary>
    private static IEnumerable<string> Blocks(string output) =>
        BlockStart().Split(output).Where(b => b.Contains('#'));

    private static string? Field(string block, string name)
    {
        var m = Regex.Match(block, $@"^\s*{name}:\s*(.+)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string Pactl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("pactl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // Field names must stay in English whatever the desktop language.
            psi.Environment["LC_ALL"] = "C";
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!_missingLogged) Log.Write("audio: pactl not found; install pulseaudio-utils to pick sound devices");
            _missingLogged = true;
            return "";
        }
    }

    [GeneratedRegex(@"(?m)^(?=\S)")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex StreamIndex();

    [GeneratedRegex(@"application\.process\.id = ""(\d+)""")]
    private static partial Regex ProcessId();
}
