using System;
using System.Runtime.InteropServices;

namespace RazerBatteryApp
{
    internal static class WindowsAudioMute
    {
        static readonly Guid DeviceEnumeratorId = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        public static bool TryReadDefaultCaptureMute(out bool muted, out string error)
        {
            muted = false;
            error = null;
            object enumeratorObject = null;
            IMMDevice device = null;
            IAudioEndpointVolume volume = null;
            try
            {
                volume = OpenDefaultCapture(out enumeratorObject, out device);
                int hr = volume.GetMute(out muted);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { Release(volume); Release(device); Release(enumeratorObject); }
        }

        public static bool TryToggleDefaultCaptureMute(out bool muted, out string error)
        {
            muted = false;
            error = null;
            object enumeratorObject = null;
            IMMDevice device = null;
            IAudioEndpointVolume volume = null;
            try
            {
                volume = OpenDefaultCapture(out enumeratorObject, out device);
                int hr = volume.GetMute(out muted);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                Guid context = Guid.Empty;
                hr = volume.SetMute(!muted, ref context);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                hr = volume.GetMute(out muted);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { Release(volume); Release(device); Release(enumeratorObject); }
        }

        static IAudioEndpointVolume OpenDefaultCapture(out object enumeratorObject, out IMMDevice device)
        {
            enumeratorObject = null;
            device = null;
            Type type = Type.GetTypeFromCLSID(DeviceEnumeratorId);
            if (type == null) throw new InvalidOperationException("Windows audio control is unavailable.");
            enumeratorObject = Activator.CreateInstance(type);
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorObject;
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Communications, out device);
            if (hr < 0) hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Multimedia, out device);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object volumeObject;
            hr = device.Activate(ref iid, 23, IntPtr.Zero, out volumeObject);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return (IAudioEndpointVolume)volumeObject;
        }

        static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try { Marshal.ReleaseComObject(value); } catch { }
            }
        }

        enum EDataFlow { Render = 0, Capture = 1, All = 2 }
        enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint channelCount);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
