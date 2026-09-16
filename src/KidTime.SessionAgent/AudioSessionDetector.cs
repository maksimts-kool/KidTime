using System.Runtime.InteropServices;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;

namespace KidTime.SessionAgent;

/// <summary>
/// Finds the applications holding an active Windows audio session, so an application a child is
/// using without looking at it - a Discord or Telegram call behind a game - is counted as used.
///
/// It asks the audio endpoint manager which sessions exist and in what state, the same thing the
/// volume mixer and the microphone-in-use indicator show. No stream is opened and no audio is
/// read. Every active endpoint is walked rather than only the default one, because a headset set
/// as the communications device is exactly where a call lives.
/// </summary>
internal static class AudioSessionDetector
{
    /// <summary>Bounds what one sample carries; the service applies the same bound.</summary>
    public const int MaximumApplications = 16;

    /// <summary>
    /// A WebView2 or Chromium host plays audio from a helper process the catalog rightly refuses
    /// to name, so the session is walked up to the application that started it - which is how
    /// Teams and WhatsApp calls land on Teams and WhatsApp.
    /// </summary>
    private const int MaximumParentDepth = 3;

    private static bool _faultReported;

    public static IReadOnlyList<AudibleApplication> GetAudibleApplications()
    {
        var capturing = new HashSet<uint>();
        var rendering = new HashSet<uint>();
        try
        {
            CollectActiveSessions(DataFlow.Capture, capturing);
            CollectActiveSessions(DataFlow.Render, rendering);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException
                                          or UnauthorizedAccessException)
        {
            // Background audio is a refinement of foreground counting, not a replacement for it,
            // so a machine whose audio stack will not answer keeps counting what is in front.
            if (!_faultReported)
            {
                _faultReported = true;
                SessionLogger.ReportFault(
                    DiagnosticSeverities.Warning,
                    "Audio sessions could not be read; background calls are not being counted.",
                    exception);
            }
            return [];
        }

        var applications = new Dictionary<string, AudibleApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var processId in capturing.Concat(rendering))
        {
            if (applications.Count >= MaximumApplications) break;
            if (ResolveApplication(processId) is not { } application) continue;
            var isCapturing = capturing.Contains(processId);
            if (applications.TryGetValue(application.ExecutablePath, out var existing))
            {
                if (isCapturing && !existing.IsCapturing)
                    applications[application.ExecutablePath] = existing with { IsCapturing = true };
                continue;
            }
            applications[application.ExecutablePath] = new AudibleApplication(application, isCapturing);
        }

        return [.. applications.Values];
    }

    private static ApplicationDescriptor? ResolveApplication(uint processId)
    {
        for (var depth = 0; depth <= MaximumParentDepth && processId != 0; depth++)
        {
            var descriptor = ForegroundDetector.DescribeProcess(processId);
            if (descriptor is not null && ApplicationCatalogPolicy.IsUserManageable(descriptor)) return descriptor;
            processId = ReadParentProcessId(processId);
        }
        return null;
    }

    private static void CollectActiveSessions(DataFlow flow, HashSet<uint> processIds)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDeviceCollection? devices = null;
        try
        {
            Check(enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out devices));
            Check(devices.GetCount(out var deviceCount));
            for (uint index = 0; index < deviceCount; index++)
            {
                IMMDevice? device = null;
                IAudioSessionManager2? manager = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    Check(devices.Item(index, out device));
                    var managerId = typeof(IAudioSessionManager2).GUID;
                    Check(device.Activate(ref managerId, ClsctxAll, IntPtr.Zero, out var activated));
                    manager = (IAudioSessionManager2)activated;
                    Check(manager.GetSessionEnumerator(out sessions));
                    Check(sessions.GetCount(out var sessionCount));
                    for (var sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                    {
                        Check(sessions.GetSession(sessionIndex, out var session));
                        try
                        {
                            if (session is not IAudioSessionControl2 control) continue;
                            if (control.GetState(out var state) != 0 || state != AudioSessionState.Active) continue;
                            // S_OK means this is the system sounds session, which belongs to nobody.
                            if (control.IsSystemSoundsSession() == 0) continue;
                            // A session shared by several processes reports a success code and no
                            // single owner; there is nobody to attribute it to.
                            if (control.GetProcessId(out var processId) != 0 || processId == 0) continue;
                            processIds.Add(processId);
                        }
                        finally { Release(session); }
                    }
                }
                finally
                {
                    Release(sessions);
                    Release(manager);
                    Release(device);
                }
            }
        }
        finally
        {
            Release(devices);
            Release(enumerator);
        }
    }

    private static uint ReadParentProcessId(uint processId)
    {
        var handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var information = new ProcessBasicInformation();
            return NtQueryInformationProcess(handle, 0, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? unchecked((uint)information.InheritedFromUniqueProcessId.ToInt64())
                : 0;
        }
        finally { CloseHandle(handle); }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private const uint DeviceStateActive = 0x1;
    private const int ClsctxAll = 0x17;

    private enum DataFlow { Render = 0, Capture = 1 }

    private enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    // Vtable order is the contract with these interfaces; methods KidTime never calls are declared
    // only to hold their slot.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, int classContext, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out AudioSessionState state);
        [PreserveSig] int GetDisplayName(IntPtr displayName);
        [PreserveSig] int SetDisplayName(IntPtr displayName, IntPtr eventContext);
        [PreserveSig] int GetIconPath(IntPtr iconPath);
        [PreserveSig] int SetIconPath(IntPtr iconPath, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(IntPtr groupingParam);
        [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(IntPtr identifier);
        [PreserveSig] int GetSessionInstanceIdentifier(IntPtr identifier);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
