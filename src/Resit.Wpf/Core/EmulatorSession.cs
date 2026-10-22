using System.Diagnostics;
using System.Net.NetworkInformation;
using Resit.Native;

namespace Resit.Core;

public sealed record StartOptions(bool ColdBoot = false, bool WipeData = false);

public sealed record BootProgress(string Stage, double Percent, TimeSpan Elapsed);

/// <summary>
/// The emulator's discovery file (%TEMP%\avd\running\pid_N.ini), which every running
/// emulator writes with its ports and gRPC token.
/// </summary>
public sealed record RunningEmulator(int Pid, string AvdName, int ConsolePort, int GrpcPort, string? GrpcToken)
{
    public static string Dir => Path.Combine(Path.GetTempPath(), "avd", "running");

    public static List<RunningEmulator> List()
    {
        var result = new List<RunningEmulator>();
        if (!Directory.Exists(Dir)) return result;
        foreach (var file in Directory.EnumerateFiles(Dir, "pid_*.ini"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file)["pid_".Length..], out var pid)) continue;
            if (!IsAlive(pid)) continue;
            IniFile ini;
            try { ini = IniFile.Load(file); }
            catch (IOException) { continue; }
            var name = ini.Get("avd.name");
            var grpc = ini.GetInt("grpc.port", 0);
            if (name is null || grpc == 0) continue;
            result.Add(new RunningEmulator(pid, name, ini.GetInt("port.serial", 5554), grpc, ini.Get("grpc.token")));
        }
        return result;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>A started (or attached-to) emulator instance.</summary>
public sealed class EmulatorSession : IAsyncDisposable
{
    public Instance Instance { get; }
    public SdkLocation Sdk { get; }
    public int ConsolePort { get; private set; }
    public int GrpcPort { get; private set; }
    public string Serial => $"emulator-{ConsolePort}";
    public Process? VmProcess { get; private set; }
    public EmulatorClient? Client { get; private set; }
    public Adb Adb => new(Sdk.Adb, Serial);
    public string LogPath => Path.Combine(Instance.Dir, "resit-emulator.log");

    public event EventHandler? Exited;

    private EmulatorSession(SdkLocation sdk, Instance instance)
    {
        Sdk = sdk;
        Instance = instance;
    }

    public static async Task<EmulatorSession> StartAsync(SdkLocation sdk, Instance instance, StartOptions options,
        IProgress<string> progress, CancellationToken ct)
    {
        var session = new EmulatorSession(sdk, instance);

        // Already running (e.g. from start.ps1)? Attach instead of starting a second copy.
        var existing = RunningEmulator.List().FirstOrDefault(r => r.AvdName == instance.Name);
        if (existing is not null)
        {
            progress.Report("Attaching to running instance…");
            session.Attach(existing);
            return session;
        }

        (session.ConsolePort, session.GrpcPort) = FindFreePorts();
        var o = instance.Options;
        var args = new List<string>
        {
            "-avd", instance.Name,
            "-port", session.ConsolePort.ToString(),
            "-grpc", session.GrpcPort.ToString(),
            "-grpc-use-token",
            "-gpu", instance.GpuMode,
            "-accel", "on",
            "-no-boot-anim",
        };
        if (!o.QuickBoot) args.Add("-no-snapshot");
        else if (options.ColdBoot) args.Add("-no-snapshot-load");
        if (options.WipeData) args.Add("-wipe-data");
        if (!o.Audio) args.Add("-no-audio");
        var features = new List<string>();
        if (!o.Vulkan) features.Add("-Vulkan");
        if (o.SoftwareVideo) features.Add("-HardwareDecoder");
        if (features.Count > 0) args.AddRange(["-feature", string.Join(',', features)]);

        progress.Report("Starting emulator…");
        var psi = new ProcessStartInfo(sdk.Emulator)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(sdk.Emulator)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["ANDROID_HOME"] = sdk.SdkRoot;
        psi.Environment["ANDROID_SDK_ROOT"] = sdk.SdkRoot;
        psi.Environment["ANDROID_AVD_HOME"] = sdk.AvdHome;

        var launcher = Process.Start(psi) ?? throw new InvalidOperationException("Could not start emulator.exe");
        var log = new StreamWriter(session.LogPath, append: false) { AutoFlush = true };
        await log.WriteLineAsync($"> emulator {string.Join(' ', args)}");
        var sync = new object();
        void Log(string? line) { if (line is not null) lock (sync) log.WriteLine(line); }
        launcher.OutputDataReceived += (_, e) => Log(e.Data);
        launcher.ErrorDataReceived += (_, e) => Log(e.Data);
        launcher.BeginOutputReadLine();
        launcher.BeginErrorReadLine();
        launcher.EnableRaisingEvents = true;
        launcher.Exited += (_, _) => { lock (sync) log.Dispose(); };

        // emulator.exe is a launcher; the VM runs in a qemu-system child that registers itself
        // in the discovery folder once its gRPC server is up. That can take a while: the
        // emulator first probes the host's network and DNS, which some networks make slow.
        var started = DateTime.UtcNow;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var running = RunningEmulator.List().FirstOrDefault(r => r.AvdName == instance.Name && r.GrpcPort == session.GrpcPort);
                if (running is not null)
                {
                    session.Attach(running);
                    break;
                }
                if (launcher.HasExited)
                    throw new InvalidOperationException($"The emulator exited during startup (code {launcher.ExitCode}). See {session.LogPath}");
                var waited = DateTime.UtcNow - started;
                if (waited > TimeSpan.FromMinutes(5))
                    throw new TimeoutException($"The emulator did not start within 5 minutes. See {session.LogPath}");
                if (waited > TimeSpan.FromSeconds(20))
                    progress.Report($"Starting emulator… ({(int)waited.TotalSeconds} s, checking the network)");
                await Task.Delay(250, ct);
            }
        }
        catch
        {
            // Don't leave a half-started emulator running behind a failed window.
            try { if (!launcher.HasExited) launcher.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        HostLimits.LearnFromLog(session.LogPath);
        session.ApplyAudioDevices();
        if (o.HighPriority && session.VmProcess is { } vm)
        {
            try { vm.PriorityClass = ProcessPriorityClass.High; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return session;
    }

    private void Attach(RunningEmulator r)
    {
        ConsolePort = r.ConsolePort;
        GrpcPort = r.GrpcPort;
        Client = new EmulatorClient(r.GrpcPort, r.GrpcToken);
        try
        {
            VmProcess = Process.GetProcessById(r.Pid);
            VmProcess.EnableRaisingEvents = true;
            VmProcess.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException)
        {
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasExited => VmProcess is null || VmProcess.HasExited;

    private CancellationTokenSource? _audioRouting;

    /// <summary>
    /// Routes the emulator's sound and microphone to the devices chosen for this instance.
    /// Windows only accepts this once the process has opened its audio, which the emulator does a
    /// little after starting, so keep retrying for a while. A newer call replaces an older one.
    /// </summary>
    public async void ApplyAudioDevices()
    {
        _audioRouting?.Cancel();
        var cts = _audioRouting = new CancellationTokenSource();
        try
        {
            bool outputDone = false, inputDone = !Instance.Microphone;
            for (int attempt = 0; attempt < 90 && !(outputDone && inputDone); attempt++)
            {
                if (HasExited) return;
                outputDone = outputDone || AudioDevices.TryRouteProcess(VmProcess!.Id, AudioDevices.Flow.Output, Instance.Options.OutputDevice);
                inputDone = inputDone || AudioDevices.TryRouteProcess(VmProcess!.Id, AudioDevices.Flow.Input, Instance.Options.InputDevice);
                if (!(outputDone && inputDone)) await Task.Delay(2000, cts.Token);
            }
            if (!outputDone) Log.Write("audio routing: the emulator never accepted the output device");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Waits for Android to finish booting, reporting rough progress: the VM coming up, adb
    /// connecting, then the system starting. A cold boot's length is unknown, so the last stage
    /// creeps towards 95% over a typical boot time.
    /// </summary>
    public async Task WaitForBootAsync(IProgress<BootProgress> progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var systemSince = (DateTime?)null;
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (HasExited) throw new InvalidOperationException($"The emulator exited during boot. See {LogPath}");
            var status = Client is null ? null : await Client.TryGetStatusAsync(ct);
            if (status?.Booted == true) break;

            string state = "";
            try
            {
                state = (await Adb.RunAsync(TimeSpan.FromSeconds(5), "get-state")).Output.Trim();
                // After a Quick Boot restore the gRPC flag can stay false, so also ask Android.
                if (state == "device" && (await Adb.ShellAsync("getprop sys.boot_completed", TimeSpan.FromSeconds(5))).Trim() == "1") break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            var elapsed = DateTime.UtcNow - started;
            if (state != "device")
            {
                progress.Report(new BootProgress("Starting the virtual device", Math.Min(30, 10 + elapsed.TotalSeconds), elapsed));
            }
            else
            {
                systemSince ??= DateTime.UtcNow;
                double t = (DateTime.UtcNow - systemSince.Value).TotalSeconds;
                progress.Report(new BootProgress("Starting Android", 35 + 60 * (1 - Math.Exp(-t / 45)), elapsed));
            }
            await Task.Delay(1000, ct);
        }
        progress.Report(new BootProgress("Ready", 100, DateTime.UtcNow - started));
    }

    /// <summary>
    /// Wakes the device, clears the lock screen and keeps the display on while the emulator
    /// runs, the way gaming emulators behave. Also switches off any phone-imitation overlay
    /// (camera cutout, rounded corners) left over from instances created with a device profile.
    /// With <paramref name="blockAds"/>, Android's Private DNS points at AdGuard's ad-blocking
    /// resolver; without it, only Resit's own setting is undone, so a DNS the user chose stays.
    /// </summary>
    public Task PrepareForPlayAsync(bool blockAds) => Adb.ShellAsync(
        "svc power stayon true; input keyevent KEYCODE_WAKEUP; wm dismiss-keyguard; locksettings set-disabled true; " +
        "for o in $(cmd overlay list android | grep '\\[x\\] com.android.internal.emulation' | sed 's/.*\\] //'); do cmd overlay disable $o; done; " +
        (blockAds
            ? $"settings put global private_dns_specifier {AdBlockDns}; settings put global private_dns_mode hostname"
            : $"if [ \"$(settings get global private_dns_specifier)\" = {AdBlockDns} ]; then settings put global private_dns_mode opportunistic; fi"),
        TimeSpan.FromSeconds(20));

    private const string AdBlockDns = "dns.adguard-dns.com";

    /// <summary>
    /// The emulator's main (display) window. Its title varies between versions ("Emulator",
    /// "Android Emulator - name:port"), so it is found by its Qt window class instead.
    /// </summary>
    public IntPtr FindMainWindow()
    {
        if (VmProcess is null) return IntPtr.Zero;
        return Win32.TopLevelWindowsOf(VmProcess.Id)
            .Where(h => Win32.IsWindowVisible(h) && Win32.GetClass(h).EndsWith("QWindowIcon"))
            .Where(h => !Win32.GetText(h).Contains("Extended", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>The emulator's own floating toolbar (Qt tool window), which Resit's sidebar replaces.</summary>
    public IEnumerable<IntPtr> FindToolbarWindows() =>
        VmProcess is null ? [] : Win32.TopLevelWindowsOf(VmProcess.Id).Where(h => Win32.GetClass(h).Contains("QWindowTool"));

    /// <summary>Saves the Quick Boot snapshot (if enabled) and exits; kills the VM if it hangs.</summary>
    public async Task StopAsync()
    {
        if (HasExited) return;
        try { await Adb.EmuKillAsync(); }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await VmProcess!.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { VmProcess!.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    private static (int Console, int Grpc) FindFreePorts()
    {
        var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        int console = 5554;
        while ((used.Contains(console) || used.Contains(console + 1)) && console < 5680) console += 2;
        int grpc = 8554;
        while (used.Contains(grpc) && grpc < 8700) grpc++;
        return (console, grpc);
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is not null) await Client.DisposeAsync();
        VmProcess?.Dispose();
    }
}
