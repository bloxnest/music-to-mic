using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MusicToMic.Audio
{
    /// <summary>
    /// The one format used inside the app: 48 kHz, stereo, 32-bit float. Windows converts each device to and
    /// from it (sample rate and mono/stereo) once, at the edge, so the mixer never converts anything itself.
    /// </summary>
    internal static class Format
    {
        public const int Rate = 48000;
        public const int Channels = 2;
        public static WaveFormat Float { get { return WaveFormat.Make(AudioConst.WAVE_FORMAT_IEEE_FLOAT, Rate, Channels, 32); } }
        public static WaveFormat Pcm16 { get { return WaveFormat.Make(AudioConst.WAVE_FORMAT_PCM, Rate, Channels, 16); } }

        public static long Hns(double milliseconds) { return (long)(milliseconds * 10000); }
    }

    /// <summary>A stereo float ring buffer. Used by one thread only (the audio thread), so it needs no locks.</summary>
    internal sealed class FloatRing
    {
        private readonly float[] data;
        private readonly int mask;   // in samples
        public long Written, Read;    // in frames

        public FloatRing(int frames)
        {
            int size = 1;
            while (size < frames * 2) size <<= 1;
            data = new float[size];
            mask = size - 1;
        }

        public int CapacityFrames { get { return data.Length / 2; } }
        public int Fill { get { return (int)(Written - Read); } }

        public float this[long frame, int channel]
        {
            get { return data[(int)((frame * 2 + channel) & mask)]; }
        }

        public void Clear() { Read = Written; }

        public unsafe void WriteFloat(IntPtr source, int frames)
        {
            float* s = (float*)source;
            long w = Written * 2;
            for (int i = 0; i < frames * 2; i++) data[(int)((w + i) & mask)] = s[i];
            Advance(frames);
        }

        public unsafe void WritePcm16(IntPtr source, int frames)
        {
            short* s = (short*)source;
            long w = Written * 2;
            for (int i = 0; i < frames * 2; i++) data[(int)((w + i) & mask)] = s[i] / 32768f;
            Advance(frames);
        }

        public void Write(float[] source, int frames)
        {
            long w = Written * 2;
            for (int i = 0; i < frames * 2; i++) data[(int)((w + i) & mask)] = source[i];
            Advance(frames);
        }

        public void WriteSilence(int frames)
        {
            long w = Written * 2;
            for (int i = 0; i < frames * 2; i++) data[(int)((w + i) & mask)] = 0;
            Advance(frames);
        }

        private void Advance(int frames)
        {
            Written += frames;
            if (Written - Read > CapacityFrames) Read = Written - CapacityFrames;   // overflow: drop the oldest
        }
    }

    /// <summary>Records from a microphone, from everything a render device plays, or from one app's audio.</summary>
    internal sealed class CaptureStream : IDisposable
    {
        private IAudioClient client;
        private IAudioCaptureClient capture;
        private readonly bool pcm16;
        public readonly string Name;

        private CaptureStream(IAudioClient client, bool pcm16, string name)
        {
            this.client = client;
            this.pcm16 = pcm16;
            Name = name;
            object service;
            Guid iid = AudioConst.IID_IAudioCaptureClient;
            AudioException.Check(client.GetService(ref iid, out service), "Couldn't open " + name);
            capture = (IAudioCaptureClient)service;
            AudioException.Check(client.Start(), "Couldn't start " + name);
        }

        /// <summary>A microphone (or, with loopback, everything a speaker device plays).</summary>
        public static CaptureStream OpenDevice(IMMDevice device, string name, bool loopback)
        {
            IAudioClient client = Devices.Activate(device, name);
            WaveFormat format = Format.Float;
            int flags = AudioConst.STREAMFLAGS_AUTOCONVERTPCM | AudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
            if (loopback) flags |= AudioConst.STREAMFLAGS_LOOPBACK;
            AudioException.Check(client.Initialize(AudioConst.SHAREMODE_SHARED, flags, Format.Hns(200), 0, ref format, IntPtr.Zero),
                "Failed to initialize " + name);
            return new CaptureStream(client, false, name);
        }

        /// <summary>
        /// One app's audio, and its child processes (Chrome plays sound from a helper process).
        /// include = false captures everything except that app instead.
        /// </summary>
        public static CaptureStream OpenProcess(int processId, bool include, string name)
        {
            int flags = AudioConst.STREAMFLAGS_LOOPBACK | AudioConst.STREAMFLAGS_AUTOCONVERTPCM | AudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
            IAudioClient client = ActivateProcessLoopback(processId, include, name);
            WaveFormat format = Format.Float;
            int hr = client.Initialize(AudioConst.SHAREMODE_SHARED, flags, Format.Hns(200), 0, ref format, IntPtr.Zero);
            if (hr >= 0) return new CaptureStream(client, false, name);
            // some Windows builds only take 16-bit PCM here
            Marshal.ReleaseComObject(client);
            client = ActivateProcessLoopback(processId, include, name);
            format = Format.Pcm16;
            AudioException.Check(client.Initialize(AudioConst.SHAREMODE_SHARED, flags, Format.Hns(200), 0, ref format, IntPtr.Zero),
                "Failed to initialize capture of " + name);
            return new CaptureStream(client, true, name);
        }

        private sealed class ActivationDone : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) { Done.Set(); }
        }

        private static IAudioClient ActivateProcessLoopback(int processId, bool include, string name)
        {
            var activation = new AudioClientActivationParams { ActivationType = 1, TargetProcessId = processId, ProcessLoopbackMode = include ? 0 : 1 };
            int size = Marshal.SizeOf(typeof(AudioClientActivationParams));
            IntPtr parameters = Marshal.AllocHGlobal(size);
            IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PropVariant)));
            try
            {
                Marshal.StructureToPtr(activation, parameters, false);
                // a BLOB PROPVARIANT: its size, then a pointer to the parameters
                var blob = new PropVariant { Type = AudioConst.VT_BLOB, Value = new IntPtr(size), Value2 = parameters };
                Marshal.StructureToPtr(blob, variant, false);
                var done = new ActivationDone();
                IActivateAudioInterfaceAsyncOperation operation;
                int hr = NativeAudio.ActivateAudioInterfaceAsync(AudioConst.ProcessLoopbackDevice, AudioConst.IID_IAudioClient,
                    variant, done, out operation);
                AudioException.Check(hr, "This version of Windows can't capture a single app's audio");
                if (!done.Done.WaitOne(5000)) throw new AudioException("Windows didn't answer when opening " + name, 0);
                int result;
                object activated;
                operation.GetActivateResult(out result, out activated);
                AudioException.Check(result, "Couldn't capture " + name);
                return (IAudioClient)activated;
            }
            catch (EntryPointNotFoundException)
            {
                throw new AudioException("This version of Windows can't capture a single app's audio", 0);
            }
            finally
            {
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(parameters);
            }
        }

        /// <summary>Moves everything recorded so far into the ring. Returns the frames moved.</summary>
        public int ReadInto(FloatRing ring)
        {
            int total = 0;
            while (true)
            {
                int packet;
                Check(capture.GetNextPacketSize(out packet));
                if (packet == 0) return total;
                IntPtr data;
                int frames, flags;
                long position, qpc;
                Check(capture.GetBuffer(out data, out frames, out flags, out position, out qpc));
                if ((flags & AudioConst.BUFFERFLAGS_SILENT) != 0) ring.WriteSilence(frames);
                else if (pcm16) ring.WritePcm16(data, frames);
                else ring.WriteFloat(data, frames);
                Check(capture.ReleaseBuffer(frames));
                total += frames;
            }
        }

        private void Check(int hr)
        {
            if (hr < 0) throw new AudioException(Name + ": " + AudioException.Describe(hr), hr);
        }

        public void Dispose()
        {
            if (client != null)
            {
                try { client.Stop(); } catch (COMException) { }
                Marshal.ReleaseComObject(capture);
                Marshal.ReleaseComObject(client);
                client = null;
            }
        }
    }

    /// <summary>Plays into a device: the virtual cable (for Roblox), or speakers (monitoring).</summary>
    internal sealed class RenderStream : IDisposable
    {
        private IAudioClient client;
        private IAudioRenderClient render;
        public readonly int BufferFrames;
        public readonly string Name;

        public RenderStream(IMMDevice device, string name, EventWaitHandle wake, double bufferMs)
        {
            Name = name;
            client = Devices.Activate(device, name);
            WaveFormat format = Format.Float;
            int flags = AudioConst.STREAMFLAGS_AUTOCONVERTPCM | AudioConst.STREAMFLAGS_SRC_DEFAULT_QUALITY;
            if (wake != null) flags |= AudioConst.STREAMFLAGS_EVENTCALLBACK;
            AudioException.Check(client.Initialize(AudioConst.SHAREMODE_SHARED, flags, Format.Hns(bufferMs), 0, ref format, IntPtr.Zero),
                "Failed to initialize " + name);
            if (wake != null) AudioException.Check(client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle()), "Failed to initialize " + name);
            uint frames;
            AudioException.Check(client.GetBufferSize(out frames), "Failed to initialize " + name);
            BufferFrames = (int)frames;
            object service;
            Guid iid = AudioConst.IID_IAudioRenderClient;
            AudioException.Check(client.GetService(ref iid, out service), "Failed to initialize " + name);
            render = (IAudioRenderClient)service;
            // start with a little silence so the device never starts empty
            Write(new float[BufferFrames * 2], Math.Min(BufferFrames, Format.Rate / 100));
            AudioException.Check(client.Start(), "Couldn't start " + name);
        }

        /// <summary>Frames waiting to be played (the latency this stream adds right now).</summary>
        public int Queued
        {
            get
            {
                uint padding;
                Check(client.GetCurrentPadding(out padding));
                return (int)padding;
            }
        }

        public unsafe void Write(float[] source, int frames)
        {
            if (frames <= 0) return;
            IntPtr data;
            Check(render.GetBuffer(frames, out data));
            float* d = (float*)data;
            for (int i = 0; i < frames * 2; i++) d[i] = source[i];
            Check(render.ReleaseBuffer(frames, 0));
        }

        private void Check(int hr)
        {
            if (hr < 0) throw new AudioException(Name + ": " + AudioException.Describe(hr), hr);
        }

        public void Dispose()
        {
            if (client != null)
            {
                try { client.Stop(); } catch (COMException) { }
                Marshal.ReleaseComObject(render);
                Marshal.ReleaseComObject(client);
                client = null;
            }
        }
    }
}
