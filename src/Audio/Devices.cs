using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MusicToMic.Audio
{
    /// <summary>A microphone or a speaker/output device, as Windows names it.</summary>
    internal sealed class AudioDevice
    {
        public string Id;
        public string Name;
        public DataFlow Flow;
        public bool IsDefault;

        /// <summary>For an output: the matching microphone Roblox should pick when this is a virtual cable, else null.</summary>
        public string CableMicName;

        public bool IsVirtualCable { get { return CableMicName != null; } }

        public override string ToString() { return Name; }
    }

    /// <summary>Lists and opens audio devices, and recognises virtual cables (VB-CABLE, VoiceMeeter, Virtual Audio Cable).</summary>
    internal static class Devices
    {
        internal static IMMDeviceEnumerator NewEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }

        public static List<AudioDevice> List(DataFlow flow)
        {
            var list = new List<AudioDevice>();
            IMMDeviceEnumerator enumerator = NewEnumerator();
            try
            {
                string defaultId = DefaultId(enumerator, flow);
                IMMDeviceCollection collection;
                if (enumerator.EnumAudioEndpoints(flow, AudioConst.DEVICE_STATE_ACTIVE, out collection) < 0) return list;
                int count;
                collection.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (collection.Item(i, out device) < 0) continue;
                    string id;
                    device.GetId(out id);
                    list.Add(new AudioDevice { Id = id, Name = NameOf(device), Flow = flow, IsDefault = id == defaultId });
                    Marshal.ReleaseComObject(device);
                }
                Marshal.ReleaseComObject(collection);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
            if (flow == DataFlow.Render)
            {
                List<AudioDevice> mics = List(DataFlow.Capture);
                foreach (AudioDevice output in list) output.CableMicName = CableMic(output.Name, mics);
            }
            list.Sort((a, b) => a.Name.CompareTo(b.Name));
            return list;
        }

        private static string DefaultId(IMMDeviceEnumerator enumerator, DataFlow flow)
        {
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(flow, Role.Console, out device) < 0) return null;
            string id;
            device.GetId(out id);
            Marshal.ReleaseComObject(device);
            return id;
        }

        /// <summary>Opens a device that's plugged in and enabled, or says clearly that it isn't.</summary>
        public static IMMDevice Get(string id, string name)
        {
            IMMDeviceEnumerator enumerator = NewEnumerator();
            try
            {
                IMMDevice device;
                int hr = enumerator.GetDevice(id, out device);
                if (hr < 0) throw new AudioException(name + " isn't connected", AudioConst.E_NOTFOUND);
                int state;
                device.GetState(out state);
                if (state != AudioConst.DEVICE_STATE_ACTIVE)
                {
                    Marshal.ReleaseComObject(device);
                    throw new AudioException(name + " is disconnected or disabled", AudioConst.E_NOTFOUND);
                }
                return device;
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        public static bool IsConnected(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            IMMDeviceEnumerator enumerator = NewEnumerator();
            try
            {
                IMMDevice device;
                if (enumerator.GetDevice(id, out device) < 0) return false;
                int state;
                device.GetState(out state);
                Marshal.ReleaseComObject(device);
                return state == AudioConst.DEVICE_STATE_ACTIVE;
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        public static IMMDevice Default(DataFlow flow)
        {
            IMMDeviceEnumerator enumerator = NewEnumerator();
            try
            {
                IMMDevice device;
                AudioException.Check(enumerator.GetDefaultAudioEndpoint(flow, Role.Console, out device),
                    flow == DataFlow.Render ? "No speakers or headphones found" : "No microphone found");
                return device;
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        public static string NameOf(IMMDevice device)
        {
            IPropertyStore store;
            if (device.OpenPropertyStore(AudioConst.STGM_READ, out store) < 0) return "Audio device";
            try
            {
                PropertyKey key = AudioConst.FriendlyName;
                PropVariant value;
                if (store.GetValue(ref key, out value) < 0) return "Audio device";
                string name = value.AsString();
                PropVariant.PropVariantClear(ref value);
                return string.IsNullOrEmpty(name) ? "Audio device" : name;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        public static IAudioClient Activate(IMMDevice device, string name)
        {
            Guid iid = AudioConst.IID_IAudioClient;
            object client;
            AudioException.Check(device.Activate(ref iid, AudioConst.CLSCTX_ALL, IntPtr.Zero, out client), "Failed to open " + name);
            return (IAudioClient)client;
        }

        /// <summary>
        /// The microphone side of a virtual cable: "CABLE Input (VB-Audio Virtual Cable)" plays into
        /// "CABLE Output (VB-Audio Virtual Cable)"; VoiceMeeter works the same way; Virtual Audio Cable
        /// uses the same name ("Line 1 (Virtual Audio Cable)") on both sides. Null if it isn't a known cable.
        /// </summary>
        public static string CableMic(string outputName, List<AudioDevice> mics)
        {
            if (!LooksLikeCable(outputName)) return null;
            var candidates = new List<string>
            {
                outputName.Replace("Input", "Output"),
                outputName.Replace("input", "output"),
                outputName,
            };
            foreach (string candidate in candidates)
                foreach (AudioDevice mic in mics)
                    if (string.Equals(mic.Name, candidate, StringComparison.OrdinalIgnoreCase)) return mic.Name;
            return null;
        }

        public static bool LooksLikeCable(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("vb-audio") || n.Contains("virtual audio cable") || n.Contains("voicemeeter") || n.Contains("cable input")
                || n.Contains("cable-a input") || n.Contains("cable-b input");
        }
    }

    /// <summary>Tells the app when devices are plugged in, unplugged, enabled or disabled.</summary>
    internal sealed class DeviceWatcher : IMMNotificationClient, IDisposable
    {
        private IMMDeviceEnumerator enumerator;

        /// <summary>Raised on a Windows audio thread with the id of the device that changed.</summary>
        public event Action<string> Changed;

        public DeviceWatcher()
        {
            enumerator = Devices.NewEnumerator();
            enumerator.RegisterEndpointNotificationCallback(this);
        }

        private void Raise(string id)
        {
            Action<string> handler = Changed;
            if (handler != null) handler(id);
        }

        public void OnDeviceStateChanged(string deviceId, int newState) { Raise(deviceId); }
        public void OnDeviceAdded(string deviceId) { Raise(deviceId); }
        public void OnDeviceRemoved(string deviceId) { Raise(deviceId); }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (role == Role.Console) Raise(defaultDeviceId); }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

        public void Dispose()
        {
            if (enumerator == null) return;
            enumerator.UnregisterEndpointNotificationCallback(this);
            Marshal.ReleaseComObject(enumerator);
            enumerator = null;
        }
    }
}
