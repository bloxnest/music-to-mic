using System;
using System.Collections.Generic;
using System.Windows.Threading;
using MusicToMic.Audio;

namespace MusicToMic
{
    internal enum Health { Idle, Ok, Warning, Error }

    /// <summary>
    /// Everything between the window and the audio engine: what's selected, starting and stopping, the
    /// status line, keeping to the chosen app (and waiting for it if it closes), and stopping safely and
    /// reconnecting when a device is unplugged. Runs on the window's thread.
    /// </summary>
    internal sealed class Router : IDisposable
    {
        private readonly Settings settings;
        private readonly RoutingEngine engine = new RoutingEngine();
        private readonly Dispatcher ui;
        private readonly DeviceWatcher watcher;
        private readonly DispatcherTimer tick, deviceSettle, saveSoon;
        private string lastError;
        private bool resumeAfterReconnect;

        public List<AudioApp> Apps = new List<AudioApp>();
        public List<AudioDevice> Mics = new List<AudioDevice>(), Outputs = new List<AudioDevice>();
        public SourceTarget Source;
        public AudioDevice Mic, Output;

        public bool Routing { get; private set; }
        public bool WaitingForSource { get; private set; }
        public bool Monitor { get; private set; }
        public string LostDevice { get; private set; }
        public string Status { get; private set; }
        public string Detail { get; private set; }
        public Health Health { get; private set; }

        /// <summary>Raised (on the window's thread) whenever anything the window shows has changed.</summary>
        public event Action Changed;

        /// <summary>Whether the window is open: the app list only needs refreshing then.</summary>
        public bool Watching;

        public Router(Settings settings)
        {
            this.settings = settings;
            ui = Dispatcher.CurrentDispatcher;
            engine.ProblemFound += (problem, detail) => ui.BeginInvoke(new Action(() => OnProblem(problem, detail)));
            watcher = new DeviceWatcher();
            watcher.Changed += id => ui.BeginInvoke(new Action(() => { deviceSettle.Stop(); deviceSettle.Start(); }));
            deviceSettle = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, delegate { deviceSettle.Stop(); OnDevicesChanged(); }, ui);
            deviceSettle.Stop();
            saveSoon = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, delegate { saveSoon.Stop(); settings.Save(); }, ui);
            saveSoon.Stop();
            tick = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, delegate { Tick(); }, ui);

            RefreshDevices();
            RefreshApps();
            PickDefaults();
            ApplyMixer();
            Logger.Write("Devices: " + Mics.Count + " microphones, " + Outputs.Count + " outputs"
                + (CableInstalled ? ", virtual cable: " + FirstCable.Name : ", no virtual cable"));
            UpdateStatus();
        }

        public RoutingEngine Engine { get { return engine; } }
        public Mixer Mixer { get { return engine.Mixer; } }
        public Settings Settings { get { return settings; } }

        public bool CableInstalled { get { return FirstCable != null; } }

        /// <summary>The virtual cable to use by default: a normal stereo one first (VB-CABLE also offers a 16-channel input).</summary>
        public AudioDevice FirstCable
        {
            get
            {
                AudioDevice any = null;
                foreach (AudioDevice d in Outputs)
                {
                    if (!d.IsVirtualCable) continue;
                    if (!Devices.IsMultiChannelCable(d.Name)) return d;
                    if (any == null) any = d;
                }
                return any;
            }
        }

        // ---------- lists and defaults ----------

        public void RefreshApps()
        {
            try { Apps = AudioApps.List(); }
            catch (Exception e) { Logger.Write("Couldn't list audio apps: " + e.Message); }
        }

        private void RefreshDevices()
        {
            AudioApps.DevicesChanged();
            Mics = Devices.List(DataFlow.Capture);
            Outputs = Devices.List(DataFlow.Render);
            Mic = Match(Mics, Mic);
            Output = Match(Outputs, Output);
        }

        /// <summary>Keeps the same device selected after the list is rebuilt (or keeps the old entry if it's gone).</summary>
        private static AudioDevice Match(List<AudioDevice> list, AudioDevice selected)
        {
            if (selected == null) return null;
            foreach (AudioDevice d in list) if (d.Id == selected.Id) return d;
            return selected;
        }

        public bool IsConnected(AudioDevice device)
        {
            if (device == null) return false;
            foreach (AudioDevice d in device.Flow == DataFlow.Capture ? Mics : Outputs) if (d.Id == device.Id) return true;
            return false;
        }

        private void PickDefaults()
        {
            Mic = ById(Mics, settings.DefaultMicId);
            if (Mic == null) foreach (AudioDevice d in Mics) if (d.IsDefault && !Devices.LooksLikeCable(d.Name)) Mic = d;
            if (Mic == null) foreach (AudioDevice d in Mics) if (!Devices.LooksLikeCable(d.Name)) { Mic = d; break; }
            Output = ById(Outputs, settings.DefaultOutputId) ?? FirstCable;

            if (settings.DefaultSourceExe.Length > 0)
            {
                AudioApp app = AudioApps.Find(settings.DefaultSourceExe);
                Source = app != null ? app.ToTarget()
                    : new SourceTarget { Exe = settings.DefaultSourceExe, Name = settings.DefaultSourceName.Length > 0 ? settings.DefaultSourceName : settings.DefaultSourceExe };
            }
            else
            {
                foreach (AudioApp app in Apps) if (app.Playing) { Source = app.ToTarget(); break; }
            }
        }

        private static AudioDevice ById(List<AudioDevice> list, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (AudioDevice d in list) if (d.Id == id) return d;
            return null;
        }

        /// <summary>The whole-PC choice for the source list: all other apps, or (older Windows) all of the speakers.</summary>
        public static SourceTarget EverythingTarget()
        {
            return AudioApps.PerAppCapture
                ? new SourceTarget { Name = "All apps (except Music to Mic)", EverythingElse = true }
                : new SourceTarget { Name = "Everything your speakers play", WholeDevice = true };
        }

        // ---------- choices ----------

        public void SelectSource(SourceTarget target)
        {
            if (target == null || (Source != null && SameSource(Source, target) && !WaitingForSource)) return;
            Source = target;
            Logger.Write("Source selected: " + target.Name);
            if (Routing)
            {
                WaitingForSource = false;
                engine.ChangeSource(target);
            }
            UpdateStatus();
        }

        private static bool SameSource(SourceTarget a, SourceTarget b)
        {
            return a.EverythingElse == b.EverythingElse && a.WholeDevice == b.WholeDevice && a.Exe == b.Exe
                && (a.Exe == null || a.ProcessId == b.ProcessId);   // same app, but restarted, counts as new
        }

        public void SelectMic(AudioDevice mic)
        {
            if (mic == null || (Mic != null && Mic.Id == mic.Id)) return;
            Mic = mic;
            Logger.Write("Microphone selected: " + mic.Name);
            RestartIfRouting();
        }

        public void SelectOutput(AudioDevice output)
        {
            if (output == null || (Output != null && Output.Id == output.Id)) return;
            Output = output;
            Logger.Write("Output selected: " + output.Name + (output.IsVirtualCable ? " (Roblox mic: " + output.CableMicName + ")" : ""));
            RestartIfRouting();
        }

        private void RestartIfRouting()
        {
            if (Routing)
            {
                engine.Stop();
                Routing = false;
                Start();
            }
            else UpdateStatus();
        }

        public RouteMode Mode
        {
            get { return settings.Mode; }
            set
            {
                if (settings.Mode == value) return;
                settings.Mode = value;
                SaveSoon();
                Logger.Write("Mode: " + ModeName(value));
                if (Routing)
                {
                    if (value != RouteMode.MusicOnly && (Mic == null || !IsConnected(Mic)))
                    {
                        Stop();
                        Fail("Choose a microphone first");
                        return;
                    }
                    engine.ChangeMode(value);
                    if (value == RouteMode.MicOnly) WaitingForSource = false;
                }
                else engine.Mixer.Mode = value;
                UpdateStatus();
            }
        }

        public static string ModeName(RouteMode mode)
        {
            return mode == RouteMode.MusicOnly ? "Music only" : mode == RouteMode.MicOnly ? "Microphone only" : "Mic + Music";
        }

        // ---------- volumes ----------

        public void SetVolumes(float music, float mic, float master)
        {
            settings.MusicVolume = Clamp(music);
            settings.MicVolume = Clamp(mic);
            settings.MasterVolume = Clamp(master);
            ApplyMixer();
            SaveSoon();
        }

        public void ApplyPreset(Preset preset)
        {
            SetVolumes(preset.Music, preset.Mic, preset.Master);
            Logger.Write("Preset: " + preset.Name);
            Raise();
        }

        public bool MuteMusic
        {
            get { return engine.Mixer.MusicMuted; }
            set { engine.Mixer.MusicMuted = value; Raise(); }
        }

        public bool MuteMic
        {
            get { return engine.Mixer.MicMuted; }
            set { engine.Mixer.MicMuted = value; Raise(); }
        }

        public bool MuteAll
        {
            get { return engine.Mixer.AllMuted; }
            set { engine.Mixer.AllMuted = value; Raise(); }
        }

        public bool PushToMusic
        {
            get { return settings.PushToMusic; }
            set
            {
                settings.PushToMusic = value;
                engine.PushKey = value ? settings.PushKey : 0;
                SaveSoon();
                Logger.Write("Push-to-music " + (value ? "on (hold " + KeyNames.Name(settings.PushKey) + ")" : "off"));
                UpdateStatus();
            }
        }

        public void SetPushKey(int vk)
        {
            settings.PushKey = vk;
            engine.PushKey = settings.PushToMusic ? vk : 0;
            settings.Save();
            Logger.Write("Push-to-music key: " + KeyNames.Name(vk));
            UpdateStatus();
        }

        public void SetMonitor(bool on)
        {
            if (Monitor == on) return;
            Monitor = on;
            if (Routing) engine.SetMonitor(on);
            Logger.Write("Monitor output " + (on ? "on" : "off"));
            Raise();
        }

        /// <summary>Monitoring with the mic on and no headphones can echo (the mic hears the speakers).</summary>
        public bool MonitorCouldEcho
        {
            get { return Monitor && Mode != RouteMode.MusicOnly; }
        }

        private void ApplyMixer()
        {
            Mixer m = engine.Mixer;
            m.MusicVolume = settings.MusicVolume;
            m.MicVolume = settings.MicVolume;
            m.MasterVolume = settings.MasterVolume;
            m.Mode = settings.Mode;
            engine.PushKey = settings.PushToMusic ? settings.PushKey : 0;
        }

        private void SaveSoon()
        {
            saveSoon.Stop();
            saveSoon.Start();
        }

        // ---------- start and stop ----------

        public bool Start()
        {
            if (Routing) return true;
            lastError = null;
            if (Output == null || !IsConnected(Output))
            {
                Fail(Output == null ? (CableInstalled ? "Choose an output" : "Virtual microphone not detected") : Output.Name + " is disconnected");
                return false;
            }
            if (Mode != RouteMode.MicOnly && Source == null)
            {
                Fail("No audio source selected");
                return false;
            }
            if (Mode != RouteMode.MusicOnly && (Mic == null || !IsConnected(Mic)))
            {
                Fail(Mic == null ? "No microphone selected" : "Microphone disconnected: " + Mic.Name);
                return false;
            }
            if (Source != null && Source.Exe != null)
            {
                AudioApp app = AudioApps.Find(Source.Exe);
                if (app != null) Source.ProcessId = app.ProcessId;
            }
            bool monitor = Monitor && !(Source != null && Source.WholeDevice);
            try
            {
                ApplyMixer();
                engine.Start(Mode == RouteMode.MicOnly ? null : Source, Mic != null ? Mic.Id : null, Mic != null ? Mic.Name : null,
                    Output.Id, Output.Name, Mode, monitor);
            }
            catch (AudioException e)
            {
                Fail("Failed to initialize audio device: " + e.Message);
                return false;
            }
            Routing = true;
            WaitingForSource = Mode != RouteMode.MicOnly && Source != null && !engine.SourceAvailable;
            LostDevice = null;
            resumeAfterReconnect = false;
            Logger.Write("Routing started: " + Describe());
            UpdateStatus();
            return true;
        }

        public void Stop()
        {
            resumeAfterReconnect = false;
            if (!Routing) return;
            engine.Stop();
            Routing = false;
            WaitingForSource = false;
            Logger.Write("Routing stopped");
            UpdateStatus();
        }

        private string Describe()
        {
            string what = Mode == RouteMode.MicOnly ? "microphone (" + Mic.Name + ")"
                : Mode == RouteMode.MusicOnly ? Source.Name
                : Source.Name + " + microphone (" + Mic.Name + ")";
            return what + " to " + Output.Name;
        }

        private void Fail(string message)
        {
            lastError = message;
            Logger.Write("Error: " + message);
            UpdateStatus();
        }

        private void OnProblem(Problem problem, string detail)
        {
            switch (problem)
            {
                case Problem.SourceLost:
                    if (!Routing || Mode == RouteMode.MicOnly) return;
                    if (!WaitingForSource) Logger.Write("Source unavailable: " + detail + " closed or stopped. Waiting for it");
                    WaitingForSource = true;
                    break;
                case Problem.MonitorLost:
                    Monitor = false;
                    Logger.Write("Monitor stopped: " + detail);
                    break;
                case Problem.OutputLost:
                case Problem.MicLost:
                    engine.Stop();
                    Routing = false;
                    WaitingForSource = false;
                    LostDevice = detail;
                    resumeAfterReconnect = settings.AutoReconnect;
                    lastError = (problem == Problem.MicLost ? "Microphone disconnected: " : "Output disconnected: ") + detail;
                    Logger.Write("Device disconnected: " + detail + ". Routing stopped" + (resumeAfterReconnect ? ", will resume when it's back" : ""));
                    RefreshDevices();
                    break;
                default:
                    engine.Stop();
                    Routing = false;
                    WaitingForSource = false;
                    lastError = "Audio stopped: " + detail;
                    Logger.Write("Audio error: " + detail);
                    break;
            }
            UpdateStatus();
        }

        private void OnDevicesChanged()
        {
            int mics = Mics.Count, outputs = Outputs.Count;
            RefreshDevices();
            if (mics != Mics.Count || outputs != Outputs.Count)
                Logger.Write("Devices changed: " + Mics.Count + " microphones, " + Outputs.Count + " outputs");
            TryResume();
            UpdateStatus();
        }

        private void TryResume()
        {
            if (!resumeAfterReconnect || Routing) return;
            if (!IsConnected(Output) || (Mode != RouteMode.MusicOnly && !IsConnected(Mic))) return;
            Logger.Write("Reconnected: " + LostDevice + ". Resuming");
            Start();
        }

        /// <summary>Every 3 s: refresh the app list while the window is open, and find a closed source app again.</summary>
        private void Tick()
        {
            bool changed = false;
            if (Watching)
            {
                RefreshApps();
                changed = true;
            }
            if (Routing && WaitingForSource && Source != null && Source.Exe != null)
            {
                AudioApp app = AudioApps.Find(Source.Exe);
                if (app != null)
                {
                    Source.ProcessId = app.ProcessId;
                    Source.Name = app.Name;
                    WaitingForSource = false;
                    engine.ChangeSource(Source);
                    Logger.Write("Source back: " + app.Name);
                    changed = true;
                }
            }
            else if (Routing && WaitingForSource && Source != null && (Source.EverythingElse || Source.WholeDevice))
            {
                WaitingForSource = false;
                engine.ChangeSource(Source);
            }
            if (resumeAfterReconnect && !Routing)
            {
                RefreshDevices();
                TryResume();
            }
            if (changed) UpdateStatus();
        }

        private void UpdateStatus()
        {
            string key = KeyNames.Name(settings.PushKey);
            if (Routing)
            {
                if (WaitingForSource)
                {
                    Health = Health.Warning;
                    Status = "Source unavailable";
                    Detail = Source.Name + " isn't playing right now (closed?). Waiting for it to come back.";
                }
                else
                {
                    Health = Health.Ok;
                    Status = "Routing";
                    Detail = (Mode == RouteMode.MicOnly ? "Your mic" : Mode == RouteMode.MusicOnly ? Source.Name : Source.Name + " + your mic")
                        + " → " + (Output.IsVirtualCable ? Output.CableMicName : Output.Name)
                        + (settings.PushToMusic && Mode != RouteMode.MicOnly ? ". Hold " + key + " to send music." : ".");
                }
            }
            else if (lastError != null)
            {
                Health = Health.Error;
                Status = lastError;
                Detail = resumeAfterReconnect ? "Routing resumes by itself when it's back." : ErrorHelp(lastError);
            }
            else if (!CableInstalled)
            {
                Health = Health.Warning;
                Status = "Virtual microphone not detected";
                Detail = "Install VB-CABLE (free) so Roblox can hear the music. See Roblox Setup.";
            }
            else
            {
                Health = Health.Idle;
                Status = "Ready";
                Detail = Output != null && Output.IsVirtualCable
                    ? "In Roblox, pick “" + Output.CableMicName + "” as your microphone."
                    : "Pick a virtual cable as the output so Roblox can use it as a mic.";
            }
            Raise();
        }

        private static string ErrorHelp(string error)
        {
            if (error.StartsWith("No audio source")) return "Pick the app to play from (it shows up once it plays sound).";
            if (error.StartsWith("Virtual microphone")) return "Install VB-CABLE (free) so Roblox can hear the music. See Roblox Setup.";
            if (error.Contains("disconnected")) return "Plug it back in, or pick another device, then Start.";
            return "Check the devices, then press Start again.";
        }

        private void Raise()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        private static float Clamp(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        public void Dispose()
        {
            tick.Stop();
            engine.Dispose();
            watcher.Dispose();
            settings.Save();
        }
    }
}
