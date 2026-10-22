namespace Resit.Platform;

/// <summary>The few facts about the host OS that the rest of Resit branches on.</summary>
public static class OS
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>A program's file name on this OS: "emulator.exe" on Windows, "emulator" elsewhere.</summary>
    public static string Exe(string name) => IsWindows ? name + ".exe" : name;

    /// <summary>The setup script for this OS, as shipped next to the app or in the repo root.</summary>
    public static string SetupScript => IsWindows ? "setup.ps1" : "setup.sh";

    /// <summary>What the "follow the system" sound device is called here.</summary>
    public static string DefaultDeviceName => IsWindows ? "Windows default" : "System default";
}
