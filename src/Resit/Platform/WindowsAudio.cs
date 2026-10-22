using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Resit.Platform;

/// <summary>
/// Windows sound devices, and per-app routing: the emulator always plays through "the default
/// device", so Resit picks the device by setting a per-app default for the emulator process, the
/// same as Settings → Sound → Volume mixer → App volume and device preferences.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAudio
{
    private const int DEVICE_STATE_ACTIVE = 1;
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    /// <summary>Active devices for one direction, by friendly name.</summary>
    public static List<AudioDevice> List(Flow flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints((int)flow, DEVICE_STATE_ACTIVE, out var devices) != 0) return result;
            devices.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0) continue;
                device.GetId(out var id);
                result.Add(new AudioDevice(id, NameOf(device) ?? id));
            }
        }
        catch (COMException)
        {
        }
        return result.OrderBy(d => d.Name).ToList();
    }

    private static string? NameOf(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) != 0) return null;
        var key = FriendlyName;
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            const ushort VT_LPWSTR = 31;
            return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    /// <summary>
    /// Sends a process's sound (or takes its microphone) to a device; null means "follow the
    /// Windows default". Windows also remembers it for that program. Returns false if this
    /// version of Windows does not offer per-app routing.
    /// </summary>
    public static bool TryRouteProcess(int processId, Flow flow, string? deviceId)
    {
        try
        {
            var factory = PolicyConfig.Value;
            if (factory is null) return false;
            IntPtr hstring = IntPtr.Zero;
            if (!string.IsNullOrEmpty(deviceId))
            {
                // The policy API wants the device-interface path, not the bare endpoint id.
                var iface = flow == Flow.Output ? "{e6327cad-dcec-4949-ae8a-991e976a79d2}" : "{2eef81be-33fa-4800-9670-1cd474972c3f}";
                var path = $@"\\?\SWD#MMDEVAPI#{deviceId}#{iface}";
                if (WindowsCreateString(path, path.Length, out hstring) != 0) return false;
            }
            try
            {
                const int eConsole = 0, eMultimedia = 1;
                int hr1 = factory.SetPersistedDefaultAudioEndpoint((uint)processId, (int)flow, eConsole, hstring);
                int hr2 = factory.SetPersistedDefaultAudioEndpoint((uint)processId, (int)flow, eMultimedia, hstring);
                return hr1 == 0 && hr2 == 0;
            }
            finally
            {
                if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            Core.Log.Write($"audio routing failed: {ex.Message}");
            return false;
        }
    }

    private static readonly Lazy<IAudioPolicyConfigFactory?> PolicyConfig = new(() =>
    {
        const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
        if (WindowsCreateString(ClassName, ClassName.Length, out var classId) != 0) return null;
        try
        {
            // The interface id changed in Windows 10 21H2 (build 21390).
            var iid = Environment.OSVersion.Version.Build >= 21390
                ? new Guid("ab3d4648-e242-459f-b02f-541c70306324")
                : new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
            if (RoGetActivationFactory(classId, ref iid, out var factory) != 0 || factory == IntPtr.Zero) return null;
            try
            {
                return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(factory);
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Core.Log.Write($"audio policy unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    });

    // ---- Interop ----------------------------------------------------------------------

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, int PropertyId);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    /// <summary>
    /// Undocumented WinRT factory behind Windows' per-app audio device settings (the layout
    /// EarTrumpet uses). It is IInspectable-based, so its three methods come first.
    /// </summary>
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactory
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void add_CtxVolumeChange();
        void remove_CtxVolumeChanged();
        void add_RingerVibrateStateChanged();
        void remove_RingerVibrateStateChange();
        void SetVolumeGroupGainForId();
        void GetVolumeGroupGainForId();
        void GetActiveVolumeGroupForEndpointId();
        void GetVolumeGroupsForEndpoint();
        void GetCurrentVolumeContext();
        void SetVolumeGroupMuteForId();
        void GetVolumeGroupMuteForId();
        void SetRingerVibrateState();
        void GetRingerVibrateState();
        void SetPreferredChatApplication();
        void ResetPreferredChatApplication();
        void GetPreferredChatApplication();
        void GetCurrentChatApplications();
        void add_ChatContextChanged();
        void remove_ChatContextChanged();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }
}
