using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MusicToMic.Audio
{
    /// <summary>Where the music comes from.</summary>
    internal sealed class SourceTarget
    {
        public string Exe;          // e.g. "spotify.exe"; null for the whole-PC options
        public string Name;         // e.g. "Spotify"
        public int ProcessId;       // the app's main process (its children are included)
        public bool EverythingElse; // all apps except Music to Mic itself
        public bool WholeDevice;    // older Windows: everything the default speakers play

        public override string ToString() { return Name; }
    }

    internal enum Problem { None, OutputLost, MicLost, SourceLost, MonitorLost, Failed }

    /// <summary>
    /// Runs the routing on its own high-priority audio thread: reads the music and the mic, mixes them,
    /// and plays the result into the virtual cable (and optionally your speakers, to monitor). Everything is
    /// paced by the output device, which wakes the thread every ~10 ms; nothing else runs in between.
    /// </summary>
    internal sealed class RoutingEngine : IDisposable
    {
        public readonly Mixer Mixer = new Mixer();

        /// <summary>How much audio to keep queued in the output, in ms (the delay this adds, about).</summary>
        public static int OutputMs = 20, InputMs = 15, MonitorMs = 40;

        private Thread thread;
        private readonly ManualResetEvent stopping = new ManualResetEvent(false);
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly AutoResetEvent commandsWaiting = new AutoResetEvent(false);
        private readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();

        // only touched on the audio thread
        private RenderStream output, monitor;
        private CaptureStream source, mic;
        private Process sourceProcess;
        private FloatRing sourceRing, micRing, monitorRing;
        private DriftReader sourceReader, micReader, monitorReader;
        private float[] musicBuffer, micBuffer, mixBuffer, monitorBuffer;
        private string micId, micName, outputName;
        private SourceTarget target;
        private bool wantMonitor;
        private string Current = "output";   // which part was working when an error came, to name it

        /// <summary>Push-to-music key (Windows virtual-key code), 0 = off. Checked on the audio thread, nothing is hooked.</summary>
        public volatile int PushKey;

        /// <summary>Raised on the audio thread. The window moves it to its own thread.</summary>
        public event Action<Problem, string> ProblemFound;

        public bool Running { get { return thread != null; } }
        public volatile bool SourceAvailable;
        public volatile int Underruns;
        public volatile float LatencyMs;

        /// <summary>Health: output top-ups so far, and the least audio the output had queued when topped up.</summary>
        public volatile int Cycles, LowestQueued = int.MaxValue;

        /// <summary>Health: audio frames received so far from the mic and from the music app.</summary>
        public long MicFrames, SourceFrames;

        /// <summary>Health: how fast each input is being read to stay in sync (1 = exactly its own speed).</summary>
        public volatile float SourceRatio = 1, MicRatio = 1;

        /// <summary>Opens everything and starts. Throws AudioException with a clear message if something can't open.</summary>
        public void Start(SourceTarget source, string micId, string micName, string outputId, string outputName, RouteMode mode, bool monitorOn)
        {
            if (thread != null) Stop();
            Mixer.Mode = mode;
            this.micId = micId;
            this.micName = micName;
            this.outputName = outputName;
            target = source;
            wantMonitor = monitorOn;
            stopping.Reset();
            Exception failure = null;
            var started = new ManualResetEvent(false);
            thread = new Thread(() => Run(outputId, started, ref failure));
            thread.IsBackground = true;
            thread.Name = "Music to Mic audio";
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
            started.WaitOne();
            if (failure != null)
            {
                thread.Join();
                thread = null;
                throw failure;
            }
        }

        public void Stop()
        {
            if (thread == null) return;
            stopping.Set();
            thread.Join(3000);
            thread = null;
        }

        /// <summary>Switches the music source while running (or reconnects it after the app reopened).</summary>
        public void ChangeSource(SourceTarget source) { Post(() => { target = source; Current = "source"; OpenSource(); }); }

        public void ChangeMode(RouteMode mode)
        {
            Mixer.Mode = mode;
            Post(() =>
            {
                Current = "source";
                OpenSource();
                Current = "mic";
                OpenMic();
            });
        }

        public void SetMonitor(bool on) { Post(() => { wantMonitor = on; Current = "monitor"; OpenMonitor(); }); }

        private void Post(Action command)
        {
            if (thread == null) return;
            commands.Enqueue(command);
            commandsWaiting.Set();
        }

        // ---------- the audio thread ----------

        private void Run(string outputId, ManualResetEvent started, ref Exception failure)
        {
            int task = 0;
            IntPtr mmcss = IntPtr.Zero;
            try { mmcss = NativeAudio.AvSetMmThreadCharacteristics("Pro Audio", ref task); }
            catch (DllNotFoundException) { }
            try
            {
                try
                {
                    sourceRing = new FloatRing(Format.Rate);
                    micRing = new FloatRing(Format.Rate);
                    monitorRing = new FloatRing(Format.Rate);
                    sourceReader = new DriftReader(sourceRing, Frames(InputMs + 5));
                    micReader = new DriftReader(micRing, Frames(InputMs));
                    monitorReader = new DriftReader(monitorRing, Frames(MonitorMs / 2));
                    IMMDevice device = Devices.Get(outputId, outputName);
                    try { output = new RenderStream(device, outputName, wake, OutputMs + 20); }
                    finally { Marshal.ReleaseComObject(device); }
                    int max = output.BufferFrames + Format.Rate / 10;
                    musicBuffer = new float[max * 2];
                    micBuffer = new float[max * 2];
                    mixBuffer = new float[max * 2];
                    monitorBuffer = new float[max * 2];
                    OpenMic();          // a missing mic stops the start (the error says which)
                    OpenSource();       // a missing app doesn't: it waits for it
                    OpenMonitor();
                }
                catch (Exception e)
                {
                    failure = e is AudioException ? e : new AudioException("Failed to initialize audio: " + e.Message, 0);
                    CloseAll();
                    return;
                }
                started.Set();
                Loop();
            }
            finally
            {
                CloseAll();
                if (mmcss != IntPtr.Zero) NativeAudio.AvRevertMmThreadCharacteristics(mmcss);
                started.Set();
            }
        }

        private void Loop()
        {
            var handles = new WaitHandle[] { stopping, wake, commandsWaiting };
            var health = Stopwatch.StartNew();
            while (true)
            {
                int which = WaitHandle.WaitAny(handles, 100);
                if (which == 0) return;
                Action command;
                while (commands.TryDequeue(out command))
                {
                    try { command(); }
                    catch (AudioException e) { if (!Report(e)) return; }
                }
                if (output == null) return;
                try
                {
                    Pump();
                }
                catch (AudioException e)
                {
                    if (!Report(e)) return;
                }
                if (health.ElapsedMilliseconds > 500)
                {
                    health.Restart();
                    CheckSourceAlive();
                }
            }
        }

        /// <summary>Self-tests: behave exactly as if the output device had just been unplugged.</summary>
        internal volatile bool UnplugOutputForTest;

        internal bool MonitorOpen { get { return monitor != null; } }

        /// <summary>One cycle: collect what arrived, top the output up to its target, feed the monitor.</summary>
        private void Pump()
        {
            if (UnplugOutputForTest)
            {
                UnplugOutputForTest = false;
                Current = "output";
                throw new AudioException(outputName + ": the device was disconnected", AudioConst.AUDCLNT_E_DEVICE_INVALIDATED);
            }
            Current = "mic";
            if (mic != null) MicFrames += mic.ReadInto(micRing);
            Current = "source";
            if (source != null) SourceFrames += source.ReadInto(sourceRing);
            Current = "output";

            int key = PushKey;
            Mixer.MusicGate = key == 0 || (GetAsyncKeyState(key) & 0x8000) != 0;

            int queued = output.Queued;
            Cycles++;
            if (queued < LowestQueued && Cycles > 20) LowestQueued = queued;
            int want = Math.Min(Frames(OutputMs) - queued, output.BufferFrames - queued);
            if (want > 0)
            {
                bool hasMusic = source != null && sourceReader.Read(musicBuffer, want);
                bool hasMic = mic != null && micReader.Read(micBuffer, want);
                Mixer.Mix(hasMusic ? musicBuffer : null, hasMic ? micBuffer : null, mixBuffer, want);
                output.Write(mixBuffer, want);
                if (monitor != null) monitorRing.Write(mixBuffer, want);
                Underruns = sourceReader.Underruns + micReader.Underruns;
                SourceRatio = (float)sourceReader.Ratio;
                MicRatio = (float)micReader.Ratio;
            }
            LatencyMs = (queued + Math.Max(0, want)) * 1000f / Format.Rate
                + (float)(source != null ? sourceReader.FillFrames : micReader.FillFrames) * 1000f / Format.Rate;

            if (monitor != null)
            {
                Current = "monitor";
                int mq = monitor.Queued;
                int mwant = Math.Min(Frames(MonitorMs) - mq, monitor.BufferFrames - mq);
                if (mwant > 0)
                {
                    monitorReader.Read(monitorBuffer, mwant);
                    monitor.Write(monitorBuffer, mwant);
                }
            }
        }

        /// <summary>Handles a device error. Returns false if routing has to stop.</summary>
        private bool Report(AudioException e)
        {
            switch (Current)
            {
                case "source":
                    CloseSource();
                    Raise(Problem.SourceLost, target != null ? target.Name : "The music app");
                    return true;
                case "monitor":
                    CloseMonitor();
                    wantMonitor = false;
                    Raise(Problem.MonitorLost, e.Message);
                    return true;
                case "mic":
                    Raise(e.DeviceGone ? Problem.MicLost : Problem.Failed, e.DeviceGone ? micName : e.Message);
                    return false;
                default:
                    Raise(e.DeviceGone ? Problem.OutputLost : Problem.Failed, e.DeviceGone ? outputName : e.Message);
                    return false;
            }
        }

        private void Raise(Problem problem, string detail)
        {
            Action<Problem, string> handler = ProblemFound;
            if (handler != null) handler(problem, detail);
        }

        private void OpenMic()
        {
            bool needed = Mixer.Mode != RouteMode.MusicOnly && !string.IsNullOrEmpty(micId);
            if (!needed)
            {
                if (mic != null) { mic.Dispose(); mic = null; }
                return;
            }
            if (mic != null) return;
            IMMDevice device = Devices.Get(micId, micName);
            try { mic = CaptureStream.OpenDevice(device, micName, false); }
            finally { Marshal.ReleaseComObject(device); }
            micReader.Reset();
        }

        private void OpenSource()
        {
            CloseSource();
            if (Mixer.Mode == RouteMode.MicOnly || target == null) return;
            try
            {
                if (target.WholeDevice)
                {
                    IMMDevice device = Devices.Default(DataFlow.Render);
                    try { source = CaptureStream.OpenDevice(device, target.Name, true); }
                    finally { Marshal.ReleaseComObject(device); }
                }
                else if (target.EverythingElse)
                {
                    source = CaptureStream.OpenProcess(Process.GetCurrentProcess().Id, false, target.Name);
                }
                else
                {
                    sourceProcess = Process.GetProcessById(target.ProcessId);
                    source = CaptureStream.OpenProcess(target.ProcessId, true, target.Name);
                }
                sourceReader.Reset();
                SourceAvailable = true;
            }
            catch (ArgumentException)   // the app already closed
            {
                CloseSource();
                Raise(Problem.SourceLost, target.Name);
            }
            catch (AudioException)
            {
                CloseSource();
                Raise(Problem.SourceLost, target.Name);
            }
        }

        /// <summary>The music app closed: stop reading it (the window waits for it to come back).</summary>
        private void CheckSourceAlive()
        {
            if (sourceProcess == null) return;
            bool gone;
            try { gone = sourceProcess.HasExited; }
            catch (InvalidOperationException) { gone = true; }
            if (!gone) return;
            CloseSource();
            Raise(Problem.SourceLost, target != null ? target.Name : "The music app");
        }

        private void OpenMonitor()
        {
            if (!wantMonitor)
            {
                CloseMonitor();
                return;
            }
            if (monitor != null) return;
            IMMDevice device = Devices.Default(DataFlow.Render);
            try { monitor = new RenderStream(device, Devices.NameOf(device), null, MonitorMs * 2 + 20); }
            finally { Marshal.ReleaseComObject(device); }
            monitorReader.Reset();
        }

        private void CloseSource()
        {
            SourceAvailable = false;
            if (source != null) { source.Dispose(); source = null; }
            if (sourceProcess != null) { sourceProcess.Dispose(); sourceProcess = null; }
        }

        private void CloseMonitor()
        {
            if (monitor != null) { monitor.Dispose(); monitor = null; }
        }

        private void CloseAll()
        {
            CloseSource();
            CloseMonitor();
            if (mic != null) { mic.Dispose(); mic = null; }
            if (output != null) { output.Dispose(); output = null; }
        }

        private static int Frames(int ms) { return Format.Rate * ms / 1000; }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

        public void Dispose()
        {
            Stop();
        }
    }
}
