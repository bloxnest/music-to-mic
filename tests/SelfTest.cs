using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using MusicToMic.Audio;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfMedia = System.Windows.Media;

namespace MusicToMic.Tests
{
    /// <summary>
    /// Self-tests. The routing tests use stand-in apps (ToneApp copied as TestSpotify.exe / TestChrome.exe) that
    /// play quiet tones on the digital output (nothing is plugged into it, so you hear nothing), route them with
    /// the real engine, and record what this process plays to check the result. Logs and settings go to
    /// tests\bin, never to %APPDATA%. Run tests\run-tests.bat [mixer drift settings devices routing window long].
    /// </summary>
    internal static class SelfTest
    {
        private static string bin;
        private static int passed, failed;
        private static readonly List<Process> started = new List<Process>();
        private const string QuietDevice = "Digital Output";   // S/PDIF: nothing plugged in

        [STAThread]
        private static int Main(string[] args)
        {
            var only = new List<string>(args);
            Func<string, bool> run = part => (only.Count == 0 && part != "long") || only.Contains(part);
            bin = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            Logger.Folder = Path.Combine(bin, "logs");
            Settings.Folder = Path.Combine(bin, "settings");
            foreach (string folder in new[] { Logger.Folder, Settings.Folder })
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            StartWithWindows.LeaveAlone = true;
            foreach (string name in new[] { "TestSpotify.exe", "TestChrome.exe" })
                File.Copy(Path.Combine(bin, "ToneApp.exe"), Path.Combine(bin, name), true);
            try
            {
                if (run("mixer")) TestMixer();
                if (run("drift")) TestDrift();
                if (run("settings")) TestSettings();
                if (run("devices")) TestDevices();
                if (run("routing")) TestRouting();
                if (run("cable")) TestCable();
                if (run("window")) TestWindow();
                if (run("long")) TestLongRun(only.Contains("long") ? 10 : 0);
            }
            catch (Exception e)
            {
                Check(false, "no crash: " + e);
            }
            finally
            {
                KillTones();
            }
            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed;
        }

        // ---------- the mixer (no devices) ----------

        private static void TestMixer()
        {
            Section("Mixer");
            var m = new Mixer();
            int n = 4800;
            float[] music = Sine(n, 440, 0.5f), mic = Sine(n, 1000, 0.5f), output = new float[n * 2];
            m.MusicVolume = 0.5f;
            m.MicVolume = 1f;
            m.Mode = RouteMode.MicAndMusic;
            Settle(m, music, mic);
            m.Mix(music, null, output, n);
            Check(Near(Peak(output), 0.25f, 0.01f), "music at 50% comes out at half level (" + Peak(output).ToString("0.000") + ")");

            m.Mode = RouteMode.MicOnly;
            Settle(m, music, mic);
            m.Mix(music, mic, output, n);
            Check(Amplitude(output, 440) < 0.002 && Near(Amplitude(output, 1000), 0.5, 0.02), "Mic only: no music, mic at full level");
            m.Mode = RouteMode.MusicOnly;
            Settle(m, music, mic);
            m.Mix(music, mic, output, n);
            Check(Amplitude(output, 1000) < 0.002 && Near(Amplitude(output, 440), 0.25, 0.02), "Music only: no mic");
            m.Mode = RouteMode.MicAndMusic;
            Settle(m, music, mic);
            m.Mix(music, mic, output, n);
            Check(Near(Amplitude(output, 440), 0.25, 0.02) && Near(Amplitude(output, 1000), 0.5, 0.02), "Mic + Music: both, each at its own volume");

            m.MusicMuted = true;
            Settle(m, music, mic);
            m.Mix(music, mic, output, n);
            Check(Amplitude(output, 440) < 0.002 && Amplitude(output, 1000) > 0.4, "Mute music silences only the music");
            m.MusicMuted = false;
            m.MicMuted = true;
            Settle(m, music, mic);
            m.Mix(music, mic, output, n);
            Check(Amplitude(output, 1000) < 0.002 && Amplitude(output, 440) > 0.2, "Mute mic silences only the mic");
            m.MicMuted = false;
            m.AllMuted = true;
            Settle(m, music, mic);
            m.Mix(music, mic, output, n);
            Check(Peak(output) < 0.001f, "Mute all silences everything");
            m.AllMuted = false;

            // push-to-music: music only while the key is held, fading in and out without a click
            m.MusicGate = false;
            Settle(m, music, mic);
            m.Mix(music, mic, output, n);
            Check(Amplitude(output, 440) < 0.002 && Amplitude(output, 1000) > 0.4, "push-to-music: key not held, no music (mic still on)");
            m.MusicGate = true;
            m.Mix(music, null, output, n);
            float jump = 0;
            for (int i = 2; i < n * 2; i += 2) jump = Math.Max(jump, Math.Abs(output[i] - output[i - 2]));
            float sineStep = (float)(0.25 * 2 * Math.PI * 440 / Format.Rate) * 1.2f;
            Check(jump <= sineStep, "...holding the key fades the music in without a click (biggest step " + jump.ToString("0.0000") + ")");

            // limiter: two loud signals together must never clip
            m.MusicVolume = 1f;
            m.MicVolume = 1f;
            float[] loud1 = Sine(n, 300, 0.95f), loud2 = Sine(n, 450, 0.95f);
            Settle(m, loud1, loud2);
            float worst = 0;
            bool clipped = false;
            for (int k = 0; k < 10; k++)
            {
                m.Mix(loud1, loud2, output, n);
                worst = Math.Max(worst, Peak(output));
                float a, b, c;
                bool clip;
                m.TakePeaks(out a, out b, out c, out clip);
                clipped |= clip;
            }
            Check(worst <= 0.892f, "the limiter keeps two loud signals under -1 dB (peak " + worst.ToString("0.000") + ", unlimited it would be 1.9)");
            Check(clipped, "...and the output meter is told it had to limit");
        }

        private static void Settle(Mixer m, float[] music, float[] mic)
        {
            var scratch = new float[music.Length];
            for (int i = 0; i < 4; i++) m.Mix(music, mic, scratch, music.Length / 2);   // let the volume glides finish
            float a, b, c;
            bool d;
            m.TakePeaks(out a, out b, out c, out d);
        }

        // ---------- drift: devices with different clocks, for hours ----------

        private static void TestDrift()
        {
            Section("Staying in sync (simulated hours)");
            foreach (double ppm in new[] { 1000.0, -500.0, 50.0 })
            {
                var ring = new FloatRing(Format.Rate);
                var reader = new DriftReader(ring, 960);
                var buffer = new float[480 * 2];
                double produced = 0;
                long written = 0;
                int underruns = 0;
                double minFill = double.MaxValue, maxFill = 0, ratioSum = 0;
                int ratioCount = 0;
                int cycles = 2 * 3600 * 100;   // two hours of 10 ms cycles
                var rng = new Random(1);
                for (int c = 0; c < cycles; c++)
                {
                    // the input device delivers 10 ms packets on its own clock, sometimes late and then two at once
                    produced += 480 * (1 + ppm / 1e6);
                    long due = (long)produced - (rng.Next(10) == 0 ? 480 : 0);
                    if (due > written)
                    {
                        ring.WriteSilence((int)(due - written));
                        written = due;
                    }
                    bool ok = reader.Read(buffer, 480);
                    if (c > 200)
                    {
                        if (!ok) underruns++;
                        minFill = Math.Min(minFill, reader.FillFrames);
                        maxFill = Math.Max(maxFill, reader.FillFrames);
                    }
                    if (c > cycles / 2)
                    {
                        ratioSum += reader.Ratio;
                        ratioCount++;
                    }
                }
                double settled = (ratioSum / ratioCount - 1) * 1e6;
                Check(underruns == 0 && maxFill - minFill < 2400 && Math.Abs(settled - ppm) < 10,
                    "input clock " + (ppm > 0 ? "+" : "") + ppm + " ppm: 2 hours, " + underruns + " gaps, delay steady within "
                    + ((maxFill - minFill) * 1000 / Format.Rate).ToString("0") + " ms, matched at " + settled.ToString("0.0") + " ppm");
            }

            // quality: while correcting speed, music comes out as clean as it went in
            foreach (double freq in new[] { 1000.0, 5000.0, 10000.0, 15000.0 })
            {
                var ring = new FloatRing(Format.Rate * 2);
                var reader = new DriftReader(ring, 960) { FixedRatio = 1.00037 };   // sweeps every in-between position
                var chunk = new float[482 * 2];
                var outBuf = new float[480 * 2];
                long n = 0;
                double errSum = 0, sigSum = 0;
                for (int c = 0; c < 400; c++)
                {
                    for (int i = 0; i < 482; i++, n++)
                        chunk[i * 2] = chunk[i * 2 + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * freq * n / Format.Rate));
                    ring.Write(chunk, 482);
                    double start = reader.Position;
                    if (!reader.Read(outBuf, 480) || c < 20) continue;
                    for (int i = 0; i < 480; i++)
                    {
                        double want = 0.5 * Math.Sin(2 * Math.PI * freq * (start + i * 1.00037) / Format.Rate);
                        errSum += (outBuf[i * 2] - want) * (outBuf[i * 2] - want);
                        sigSum += want * want;
                    }
                }
                double dB = 10 * Math.Log10(errSum / sigSum);
                Check(dB < (freq > 12000 ? -40 : -60), "speed correction keeps " + (freq / 1000) + " kHz clean: error " + dB.ToString("0") + " dB below the music");
            }

            // an app that pauses and resumes
            {
                var ring = new FloatRing(Format.Rate);
                var reader = new DriftReader(ring, 960);
                var buf = new float[960];
                for (int c = 0; c < 50; c++) { ring.Write(Sine(480, 440, 0.3f), 480); reader.Read(buf, 480); }
                bool silentWhilePaused = true;
                for (int c = 0; c < 50; c++)
                {
                    bool played = reader.Read(buf, 480);
                    if (c >= 4) silentWhilePaused &= !played && Peak(buf) == 0;   // the last ~20 ms already buffered plays out first
                }
                bool back = false;
                for (int c = 0; c < 10; c++) { ring.Write(Sine(480, 440, 0.3f), 480); back |= reader.Read(buf, 480) && Peak(buf) > 0.2f; }
                Check(silentWhilePaused && back, "a paused app gives silence, and plays again as soon as it resumes");
            }
        }

        // ---------- settings ----------

        private static void TestSettings()
        {
            Section("Settings");
            var s = new Settings
            {
                Mode = RouteMode.MusicOnly, MusicVolume = 0.3f, MicVolume = 0.8f, MasterVolume = 0.9f, PushToMusic = true, PushKey = 0x05,
                DefaultSourceExe = "spotify.exe", DefaultSourceName = "Spotify", StartMinimized = true, AutoReconnect = false,
            };
            s.Custom.Add(new Preset { Name = "Quiet", Music = 0.1f, Mic = 1f, Master = 1f });
            s.Save();
            Settings l = Settings.Load();
            Check(l.Mode == RouteMode.MusicOnly && Near(l.MusicVolume, 0.3f, 0.001f) && Near(l.MicVolume, 0.8f, 0.001f) && l.PushToMusic && l.PushKey == 0x05
                && l.DefaultSourceExe == "spotify.exe" && l.StartMinimized && !l.AutoReconnect && l.Custom.Count == 1 && l.Custom[0].Name == "Quiet",
                "mode, volumes, hotkey, default source, options and custom presets survive a restart");
            l.RememberVolumes = false;
            l.RememberMode = false;
            l.Save();
            Settings fresh = Settings.Load();
            Check(Near(fresh.MusicVolume, 0.5f, 0.001f) && fresh.Mode == RouteMode.MicAndMusic, "with Remember turned off, volumes and mode start fresh");
            Check(KeyNames.Problem(0x77) == null && KeyNames.Problem(0x05) == null && KeyNames.Problem(0x01) != null && KeyNames.Problem(0x1B) != null,
                "F8 and mouse button 4 can be the push-to-music key; the left mouse button and Esc can't");
            Check(KeyNames.Name(0x77) == "F8" && KeyNames.Name(0x05) == "Mouse 4" && KeyNames.Name(0x4B) == "K", "keys get readable names");
            new Settings().Save();
        }

        // ---------- devices ----------

        private static void TestDevices()
        {
            Section("Devices");
            List<AudioDevice> mics = Devices.List(DataFlow.Capture), outputs = Devices.List(DataFlow.Render);
            Check(mics.Count > 0 && outputs.Count > 0, "finds microphones (" + mics.Count + ") and outputs (" + outputs.Count + ")");
            Check(mics.Count(d => d.IsDefault) == 1 && outputs.Count(d => d.IsDefault) == 1, "knows the Windows default devices");
            var fakeMics = new List<AudioDevice>
            {
                new AudioDevice { Name = "CABLE Output (VB-Audio Virtual Cable)" },
                new AudioDevice { Name = "VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)" },
                new AudioDevice { Name = "Line 1 (Virtual Audio Cable)" },
                new AudioDevice { Name = "SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)" },
            };
            Check(Devices.CableMic("CABLE Input (VB-Audio Virtual Cable)", fakeMics) == "CABLE Output (VB-Audio Virtual Cable)", "VB-CABLE: plays into CABLE Input, Roblox uses CABLE Output");
            Check(Devices.CableMic("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", fakeMics) == "VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)", "VoiceMeeter is recognised too");
            Check(Devices.CableMic("Line 1 (Virtual Audio Cable)", fakeMics) == "Line 1 (Virtual Audio Cable)", "...and Virtual Audio Cable");
            Check(Devices.CableMic("Speakers (Realtek(R) Audio)", fakeMics) == null
                && Devices.CableMic("SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)", fakeMics) == null,
                "speakers and Sonar aren't mistaken for a virtual mic");
            Check(Devices.CableMic("CABLE Input (VB-Audio Virtual Cable)", new List<AudioDevice>()) == null, "a cable without its mic side doesn't count");
            Check(Devices.CableMic("Speakers (VB-Audio Virtual Cable)", fakeMics) == "CABLE Output (VB-Audio Virtual Cable)"
                && Devices.CableMic("CABLE In 16 Ch (VB-Audio Virtual Cable)", fakeMics) == "CABLE Output (VB-Audio Virtual Cable)",
                "VB-CABLE is found even when Windows calls its input “Speakers”, and its 16-channel input too");
            var twoLines = new List<AudioDevice> { new AudioDevice { Name = "Line 1 (Virtual Audio Cable)" }, new AudioDevice { Name = "Line 2 (Virtual Audio Cable)" } };
            Check(Devices.CableMic("Line 2 (Virtual Audio Cable)", twoLines) == "Line 2 (Virtual Audio Cable)", "...while Virtual Audio Cable lines still pair by their exact names");
            AudioDevice cable = outputs.FirstOrDefault(d => d.IsVirtualCable);
            Console.WriteLine("        (virtual cable on this PC: " + (cable == null ? "none" : cable.Name + " -> " + cable.CableMicName)
                + "; Windows defaults: " + outputs.First(d => d.IsDefault).Name + " / " + mics.First(d => d.IsDefault).Name + ")");
            Check(Devices.IsMultiChannelCable("CABLE In 16ch (VB-Audio Virtual Cable)") && Devices.IsMultiChannelCable("CABLE In 16 Ch (VB-Audio Virtual Cable)")
                && !Devices.IsMultiChannelCable("Speakers (VB-Audio Virtual Cable)"), "the 16-channel cable input is told apart from the normal one");
        }

        // ---------- routing with real devices ----------

        private static void TestRouting()
        {
            Section("Per-app capture and routing");
            AudioDevice quiet = Devices.List(DataFlow.Render).FirstOrDefault(d => d.Name.Contains(QuietDevice));
            if (quiet == null)
            {
                Check(false, "needs an output nothing is plugged into (" + QuietDevice + ")");
                return;
            }
            Process spotify = Tone("TestSpotify.exe", 440, 0.2, "");
            Process chrome = Tone("TestChrome.exe", 660, 0.2, " --child");   // plays from a helper process, like Chrome
            PumpUntil(() => AudioApps.List().Count(a => a.Exe.StartsWith("test")) == 2, 5);
            List<AudioApp> apps = AudioApps.List();
            AudioApp sp = apps.FirstOrDefault(a => a.Exe == "testspotify.exe"), ch = apps.FirstOrDefault(a => a.Exe == "testchrome.exe");
            Check(sp != null && ch != null, "both stand-in apps show up in the source list");
            Check(ch != null && ch.ProcessId == chrome.Id, "an app playing from a helper process is listed as the app itself (like Chrome)");
            Check(sp != null && sp.Playing, "...and marked as playing");

            new Settings().Save();
            var router = new Router(Settings.Load());
            router.SelectOutput(quiet);
            router.SelectSource(sp.ToTarget());
            router.Mode = RouteMode.MusicOnly;
            router.SetVolumes(0.5f, 1f, 1f);
            Check(router.Start(), "routing starts: " + router.Status);
            Capture own = Capture.Of(Process.GetCurrentProcess().Id);
            Pump(1.5);
            float[] got = own.Last(1.0);
            Check(Near(Band(got, 440), 0.1, 0.01) && Band(got, 660) < 0.003,
                "Spotify only: its tone comes through at 50% (" + Band(got, 440).ToString("0.000") + ") and the other app's doesn't (" + Band(got, 660).ToString("0.0000") + ")");

            router.SelectSource(ch.ToTarget());
            Pump(1.5);
            got = own.Last(1.0);
            Check(Near(Band(got, 660), 0.1, 0.01) && Band(got, 440) < 0.003,
                "switching to Chrome (helper process): only Chrome's tone (" + Band(got, 660).ToString("0.000") + ")");

            router.SelectSource(Router.EverythingTarget());
            Pump(1.5);
            got = own.Last(1.0);
            Check(Near(Band(got, 440), 0.1, 0.015) && Near(Band(got, 660), 0.1, 0.015),
                "“All apps (except Music to Mic)” takes both apps, and never its own output (no echo loop)");
            router.SelectSource(ch.ToTarget());
            Pump(1.0);

            router.Mode = RouteMode.MicAndMusic;
            Pump(1.5);
            got = own.Last(1.0);
            Check(router.Routing && Near(Band(got, 660), 0.1, 0.012), "Mic + Chrome: the mic opens and the music stays at its level");
            long micBefore = router.Engine.MicFrames;
            Pump(1.0);
            long micPerSecond = router.Engine.MicFrames - micBefore;
            Check(micPerSecond > 40000 && micPerSecond < 56000 && router.Engine.MicRatio > 0.995 && router.Engine.MicRatio < 1.005,
                "...the mic delivers audio (" + micPerSecond + " frames in a second) and is read in step with the output");
            router.SelectSource(sp.ToTarget());
            Pump(1.5);
            got = own.Last(1.0);
            Check(Near(Band(got, 440), 0.1, 0.012), "Mic + Spotify");

            router.Mode = RouteMode.MicOnly;
            Pump(1.0);
            got = own.Last(0.5);
            Check(Band(got, 440) < 0.003 && router.Routing, "Mic only: no music");
            router.Mode = RouteMode.MusicOnly;
            Pump(1.0);

            router.MuteMusic = true;
            Pump(0.8);
            Check(Band(own.Last(0.5), 440) < 0.003, "Mute music works while routing");
            router.MuteMusic = false;
            router.PushToMusic = true;   // F8 isn't held during the test
            Pump(0.8);
            Check(Band(own.Last(0.5), 440) < 0.003, "push-to-music: nothing while the key isn't held");
            router.PushToMusic = false;
            Pump(1.0);
            Check(Near(Band(own.Last(0.5), 440), 0.1, 0.012), "...and the music is back when push-to-music is off");

            // delay: beeps in, beeps out
            KillProcess(spotify);
            PumpUntil(() => router.WaitingForSource, 4);
            Process beeper = Tone("TestSpotify.exe", 1000, 0.3, " --beeps");
            AudioApp beepApp = null;
            PumpUntil(() => (beepApp = AudioApps.Find("testspotify.exe")) != null && beepApp.ProcessId == beeper.Id, 5);
            router.SelectSource(beepApp.ToTarget());
            Capture input = Capture.Of(beeper.Id);
            Pump(2.5);
            double delay = Delay(input, own);
            if (delay < 0)
            {
                float[] ia = input.Last(2.0), ob = own.Last(2.0);
                Console.WriteLine("        (no beep found: input " + ia.Length / 2 + " frames peak " + Peak(ia).ToString("0.000") + " onset " + LastOnset(ia)
                    + "; output " + ob.Length / 2 + " frames peak " + Peak(ob).ToString("0.000") + " onset " + LastOnset(ob)
                    + "; beeper alive " + !beeper.HasExited + ", source " + router.Source.Name + " pid " + router.Source.ProcessId + " vs " + beeper.Id + ")");
            }
            Check(delay > 0 && delay < 120, "delay from the app to the virtual mic: " + delay.ToString("0") + " ms (engine says " + router.Engine.LatencyMs.ToString("0") + " ms)");
            input.Dispose();

            // the source app closes and comes back
            KillProcess(beeper);
            PumpUntil(() => router.WaitingForSource, 4);
            Check(router.WaitingForSource && router.Routing && router.Status == "Source unavailable",
                "closing the app shows “Source unavailable” and keeps routing (" + router.Detail + ")");
            Pump(2.5);
            Check(router.WaitingForSource, "...it waits for it instead of switching to another app (Chrome is still playing)");
            Process again = Tone("TestSpotify.exe", 440, 0.2, "");
            PumpUntil(() => !router.WaitingForSource, 8);
            Pump(1.5);
            Check(!router.WaitingForSource && Near(Band(own.Last(0.5), 440), 0.1, 0.012), "reopening the app picks it up again by itself");

            // the output device is unplugged (simulated), and comes back
            router.Settings.AutoReconnect = true;
            router.Engine.UnplugOutputForTest = true;
            PumpUntil(() => !router.Routing, 3);
            Check(!router.Routing && router.LostDevice == quiet.Name && router.Health == Health.Error,
                "an unplugged output stops routing safely and names the device (" + router.Status + ")");
            PumpUntil(() => router.Routing, 5);
            Check(router.Routing, "with Reconnect on, routing resumes by itself once the device is there");

            router.MuteAll = true;   // the monitor plays on your real speakers: keep the test silent
            Pump(0.3);
            router.SetMonitor(true);
            Pump(0.6);
            Check(router.Engine.MonitorOpen, "Monitor output opens on the speakers (muted for the test)");
            router.SetMonitor(false);
            Pump(0.4);
            Check(!router.Engine.MonitorOpen, "...and closes");
            router.MuteAll = false;
            Check(router.Engine.Underruns == 0 || router.Engine.Underruns < 5, "no gaps while routing (" + router.Engine.Underruns + ")");

            router.Stop();
            Check(!router.Routing && router.Status != "Routing", "Stop stops it");
            own.Dispose();
            router.Dispose();
            KillProcess(again);
            KillProcess(chrome);
        }

        // ---------- end to end through the virtual cable (what Roblox hears) ----------

        private static void TestCable()
        {
            Section("Through the virtual cable (what Roblox hears)");
            new Settings().Save();
            var router = new Router(Settings.Load());
            AudioDevice cable = router.FirstCable;
            if (cable == null)
            {
                Console.WriteLine("        (skipped: no virtual cable installed)");
                return;
            }
            AudioDevice cableMic = router.Mics.First(m => m.Name == cable.CableMicName);
            Check(router.Output != null && router.Output.Id == cable.Id && !Devices.IsMultiChannelCable(cable.Name),
                "the app picks the virtual cable by itself: " + cable.Name + " (Roblox mic: " + cable.CableMicName + ")");
            Check(router.Status == "Ready" && router.Detail.Contains(cable.CableMicName), "status: " + router.Status + ". " + router.Detail);

            Process spotify = Tone("TestSpotify.exe", 440, 0.2, "");
            Process chrome = Tone("TestChrome.exe", 660, 0.2, " --child");
            AudioApp sp = null;
            PumpUntil(() => (sp = AudioApps.Find("testspotify.exe")) != null, 5);
            router.SelectSource(sp.ToTarget());
            router.Mode = RouteMode.MusicOnly;
            router.SetVolumes(0.5f, 1f, 1f);
            Check(router.Start(), "routing into the cable starts");
            Capture roblox = Capture.OfDevice(cableMic);
            Pump(2.0);
            float[] got = roblox.Last(1.0);
            Check(Near(Band(got, 440), 0.1, 0.012) && Band(got, 660) < 0.003,
                "the virtual mic gets Spotify at 50% (" + Band(got, 440).ToString("0.000") + ") and nothing from Chrome (" + Band(got, 660).ToString("0.0000") + ")");

            router.Mode = RouteMode.MicAndMusic;
            Pump(1.5);
            got = roblox.Last(1.0);
            Check(router.Routing && Near(Band(got, 440), 0.1, 0.015), "Mic + Spotify reach the virtual mic together");
            router.Mode = RouteMode.MusicOnly;

            // delay, app to virtual mic
            KillProcess(spotify);
            PumpUntil(() => router.WaitingForSource, 4);
            Process beeper = Tone("TestSpotify.exe", 1000, 0.3, " --beeps");
            PumpUntil(() => !router.WaitingForSource, 8);
            Capture input = Capture.Of(beeper.Id);
            Pump(2.5);
            double delay = Delay(input, roblox);
            Check(delay > 0 && delay < 150, "delay from the app to Roblox's mic: " + delay.ToString("0") + " ms");
            input.Dispose();
            KillProcess(beeper);
            PumpUntil(() => router.WaitingForSource, 4);
            Process steady = Tone("TestSpotify.exe", 440, 0.2, "");
            PumpUntil(() => !router.WaitingForSource, 8);

            // the cable runs on its own clock: two minutes to check nothing drifts or drops
            Pump(20);
            float low = float.MaxValue, high = 0;
            int good = 0, checks = 0;
            for (int i = 0; i < 20; i++)
            {
                Pump(5);
                low = Math.Min(low, router.Engine.LatencyMs);
                high = Math.Max(high, router.Engine.LatencyMs);
                checks++;
                if (Near(Band(roblox.Last(1.0), 440), 0.1, 0.012)) good++;
            }
            Check(good == checks && high - low < 15 && router.Engine.Underruns == 0,
                "100 s against the cable's own clock: tone right in " + good + "/" + checks + " checks, delay " + low.ToString("0") + "-" + high.ToString("0")
                + " ms, speed matched at " + ((router.Engine.SourceRatio - 1) * 1e6).ToString("0") + " ppm, " + router.Engine.Underruns + " gaps");

            router.Stop();
            roblox.Dispose();
            router.Dispose();
            KillProcess(steady);
            KillProcess(chrome);
        }

        /// <summary>Time between a beep going into the source app and it coming out of this process.</summary>
        private static double Delay(Capture input, Capture output)
        {
            float[] a = input.Last(2.0), b = output.Last(2.0);
            // both recordings end at (nearly) the same moment; pair the newest output beep with the input beep just before it
            List<int> ins = Onsets(a), outs = Onsets(b);
            if (ins.Count == 0 || outs.Count == 0) return -1;
            int frames = Math.Min(a.Length, b.Length) / 2;
            int ob = outs[outs.Count - 1] - (b.Length / 2 - frames);
            int best = -1;
            foreach (int i in ins)
            {
                int ia = i - (a.Length / 2 - frames);
                if (ia < ob) best = ia;
            }
            return best < 0 ? -1 : (ob - best) * 1000.0 / Format.Rate;
        }

        private static int LastOnset(float[] s)
        {
            List<int> all = Onsets(s);
            return all.Count == 0 ? -1 : all[all.Count - 1];
        }

        private static List<int> Onsets(float[] s)
        {
            var found = new List<int>();
            int frames = s.Length / 2;
            for (int i = 240; i < frames; i++)
            {
                if (Math.Abs(s[i * 2]) < 0.05f) continue;
                bool quietBefore = true;
                for (int j = i - 240; j < i - 12; j++) if (Math.Abs(s[j * 2]) > 0.02f) { quietBefore = false; break; }   // the beep's first rising samples don't count
                if (quietBefore) found.Add(i);
            }
            return found;
        }

        // ---------- the window ----------

        private static void TestWindow()
        {
            Section("Window, tray menu and meters");
            new Settings().Save();
            var w = new MainWindow(null, null);
            w.ShutDown = delegate { };
            Process tone = null;
            try
            {
                Forms.ContextMenuStrip menu = w.TrayMenu;
                string shown = string.Join(" | ", menu.Items.Cast<Forms.ToolStripItem>().Select(i => i is Forms.ToolStripSeparator ? "-" : i.Text));
                Check(shown == "Routing On | Mute Music | Mute Mic | - | Open Music to Mic | - | Exit", "tray menu: " + shown);

                string shots = Path.Combine(bin, "screens");
                Directory.CreateDirectory(shots);
                w.ShowFromTray();
                w.Window.Topmost = true;
                Pump(1.0);
                Check(ContentFits(w), "the main page fits (" + Overflow(w) + ")");
                Snap(w, Path.Combine(shots, "main.png"));
                var heights = new List<int> { Bounds(w).Height };
                IntPtr bigIcon = Native.SendMessage(new Wpf.Interop.WindowInteropHelper(w.Window).Handle, 0x007F, new IntPtr(1), IntPtr.Zero);
                int taskbarSize = (int)Math.Round(24 * WpfMedia.VisualTreeHelper.GetDpi(w.Window).DpiScaleX);
                Check(bigIcon != IntPtr.Zero && Icon.FromHandle(bigIcon).Size.Width == taskbarSize, "the taskbar gets a sharp " + taskbarSize + " px icon");

                // presets
                var presetBox = w.Find<WpfControls.ComboBox>("PresetBox");
                var items = (List<Choice>)presetBox.ItemsSource;
                Check(items.Select(c => c.Label).Take(3).SequenceEqual(new[] { "Music", "Background Music", "Balanced" }) && items.Last().Label == "Custom",
                    "presets: Music, Background Music, Balanced, Custom");
                presetBox.SelectedItem = items.First(c => c.Label == "Background Music");
                Pump(0.2);
                Check(Near((float)w.Find<WpfControls.Slider>("MusicSlider").Value, 25, 0.5f) && Near((float)w.Find<WpfControls.Slider>("MicSlider").Value, 100, 0.5f),
                    "Background Music sets music 25%, mic 100%");
                w.Find<WpfControls.Slider>("MusicSlider").Value = 40;
                Pump(0.2);
                Check(((Choice)presetBox.SelectedItem).Label == "Custom", "moving a slider shows Custom");
                Click(w, "SavePresetButton");
                w.Find<WpfControls.TextBox>("PresetName").Text = "Chill";
                Click(w, "PresetOkButton");
                Pump(0.2);
                Check(Settings.Load().Custom.Any(p => p.Name == "Chill" && Near(p.Music, 0.4f, 0.01f)) && ((Choice)presetBox.SelectedItem).Label == "Chill",
                    "a custom preset is saved and selected");

                // pages
                w.ShowPanel(Page.Settings);
                Pump(0.6);
                Check(ContentFits(w), "the Settings page fits (" + Overflow(w) + ")");
                heights.Add(Bounds(w).Height);
                Snap(w, Path.Combine(shots, "settings.png"));
                w.StartKeyCapture();
                Check(!w.OfferKey(0x1B) && w.KeyMessage.Contains("Esc"), "Esc is refused as the push-to-music key, with a reason");
                Check(w.OfferKey(0x05) && Settings.Load().PushKey == 0x05, "mouse button 4 is accepted and saved");
                w.ShowPanel(Page.Setup);
                Pump(0.6);
                Check(ContentFits(w), "the Roblox Setup page fits (" + Overflow(w) + ")");
                heights.Add(Bounds(w).Height);
                Check(HasText(w.Find<Wpf.FrameworkElement>("SetupPanel"), w.Router.CableInstalled ? "Virtual microphone found" : "Virtual microphone not detected"),
                    "Roblox Setup says whether a virtual cable is installed");
                Snap(w, Path.Combine(shots, "setup.png"));
                w.ShowPanel(Page.Log);
                Pump(0.6);
                Check(w.Find<WpfControls.TextBlock>("LogText").Text.Contains("Preset saved: Chill"), "the activity log shows what happened");
                heights.Add(Bounds(w).Height);
                Check(heights.All(h => h == heights[0]), "the window keeps one height on every page (" + string.Join(", ", heights) + " px)");
                w.ShowPanel(Page.Main);
                Pump(0.6);

                // idle: nothing animates
                double idle = CpuOver(3);
                Check(idle < 1.5, "the open window idles at " + idle.ToString("0.0") + "% of one CPU core while not routing");

                // routing with meters
                AudioDevice quiet = Devices.List(DataFlow.Render).First(d => d.Name.Contains(QuietDevice));
                tone = Tone("TestSpotify.exe", 440, 0.3, "");
                AudioApp app = null;
                PumpUntil(() => (app = AudioApps.Find("testspotify.exe")) != null, 5);
                w.Router.SelectOutput(quiet);
                w.Router.SelectSource(app.ToTarget());
                w.Router.Mode = RouteMode.MusicOnly;
                Click(w, "StartButton");
                Pump(1.0);
                Check(w.Router.Routing, "Start routing works from the button (" + w.Router.Status + ")");
                var cover = w.Find<Wpf.FrameworkElement>("MusicMeterCover");
                var meter = w.Find<Wpf.FrameworkElement>("MusicMeter");
                double lit = 1 - cover.ActualWidth / meter.ActualWidth;
                Check(lit > 0.5, "the music meter shows the level (" + (lit * 100).ToString("0") + "% lit)");
                Snap(w, Path.Combine(shots, "routing.png"));
                double routingCpu = CpuOver(3);
                Check(routingCpu < 6, "routing with the meters on screen uses " + routingCpu.ToString("0.0") + "% of one CPU core");
                // minimized: the window hides in the tray and routing keeps going
                w.HideToTray();
                Pump(0.8);
                long before = w.Router.Engine.SourceFrames;
                Pump(2.0);
                long during = w.Router.Engine.SourceFrames - before;
                double hiddenCpu = CpuOver(3);
                Check(!w.Window.IsVisible && w.Router.Routing && during > 80000,
                    "minimized to the hidden icons, routing keeps going (" + during + " frames of music in 2 s)");
                Check(hiddenCpu < 3, "...using " + hiddenCpu.ToString("0.0") + "% of one CPU core with the window hidden");
                w.ShowFromTray();
                Pump(0.8);

                Click(w, "StopButton");
                Pump(0.5);
                Check(!w.Router.Routing, "Stop works from the button");

                Rectangle at = Bounds(w);
                menu.Show(new Point(at.Right + 12, at.Top + 40));
                Pump(0.6);
                SnapRect(menu.Bounds, Path.Combine(shots, "tray-menu.png"));
                menu.Close();
                Check(true, "screenshots saved to tests\\bin\\screens");
                menu.Items[menu.Items.Count - 1].PerformClick();   // Exit
                Pump(0.5);
                Check(!w.Window.IsVisible && LogHas("Music to Mic closed"), "Exit closes the app");
            }
            finally
            {
                w.Quit();
                Pump(0.3);
                if (tone != null) KillProcess(tone);
            }
        }

        // ---------- a long run ----------

        private static void TestLongRun(int minutes)
        {
            if (minutes == 0) return;
            Section("Long run (" + minutes + " minutes)");
            AudioDevice quiet = Devices.List(DataFlow.Render).First(d => d.Name.Contains(QuietDevice));
            Process tone = Tone("TestSpotify.exe", 440, 0.2, " --seconds=" + (minutes * 60 + 60));
            AudioApp app = null;
            PumpUntil(() => (app = AudioApps.Find("testspotify.exe")) != null, 5);
            new Settings().Save();
            var router = new Router(Settings.Load());
            router.SelectOutput(quiet);
            router.SelectSource(app.ToTarget());
            router.Mode = RouteMode.MicAndMusic;
            router.SetVolumes(0.5f, 1f, 1f);
            router.Start();
            Capture own = Capture.Of(Process.GetCurrentProcess().Id);
            var clock = Stopwatch.StartNew();
            float minLatency = float.MaxValue, maxLatency = 0;
            long memoryStart = 0;
            TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            int checks = 0, good = 0;
            while (clock.Elapsed.TotalMinutes < minutes)
            {
                Pump(10);
                if (clock.Elapsed.TotalSeconds > 30)
                {
                    if (memoryStart == 0) memoryStart = GC.GetTotalMemory(true);
                    minLatency = Math.Min(minLatency, router.Engine.LatencyMs);
                    maxLatency = Math.Max(maxLatency, router.Engine.LatencyMs);
                }
                if (clock.Elapsed.TotalSeconds < 30) continue;   // let the speed correction settle first
                checks++;
                if (Near(Band(own.Last(1.0), 440), 0.1, 0.012)) good++;
            }
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
            long memoryEnd = GC.GetTotalMemory(true);
            Check(router.Routing && good == checks, "routed for " + minutes + " minutes: the tone was right in " + good + " of " + checks + " checks");
            Check(maxLatency - minLatency < 15, "delay stayed steady: " + minLatency.ToString("0") + "-" + maxLatency.ToString("0") + " ms (no drift)");
            Check(router.Engine.Underruns < 3, "gaps: " + router.Engine.Underruns);
            Check(cpu < 3, "CPU while routing: " + cpu.ToString("0.00") + "% of one core");
            Check(memoryEnd - memoryStart < 2 * 1024 * 1024, "memory steady (" + ((memoryEnd - memoryStart) / 1024) + " KB change)");
            router.Stop();
            own.Dispose();
            router.Dispose();
            KillProcess(tone);
        }

        // ---------- helpers ----------

        /// <summary>Records one process's audio (process loopback) on a background thread.</summary>
        private sealed class Capture : IDisposable
        {
            private readonly FloatRing ring = new FloatRing(Format.Rate * 8);
            private readonly CaptureStream stream;
            private readonly Thread thread;
            private volatile bool stop;
            private readonly object gate = new object();

            private Capture(Func<CaptureStream> open)
            {
                var ready = new ManualResetEvent(false);
                CaptureStream s = null;
                thread = new Thread(() =>
                {
                    s = open();
                    ready.Set();
                    while (!stop)
                    {
                        lock (gate) s.ReadInto(ring);
                        Thread.Sleep(5);
                    }
                    s.Dispose();
                });
                thread.IsBackground = true;
                thread.Start();
                ready.WaitOne(5000);
                stream = s;
            }

            public static Capture Of(int pid) { return new Capture(() => CaptureStream.OpenProcess(pid, true, "recording")); }

            /// <summary>Records a microphone (e.g. CABLE Output): exactly what an app like Roblox gets from it.</summary>
            public static Capture OfDevice(AudioDevice mic)
            {
                return new Capture(() =>
                {
                    IMMDevice device = Devices.Get(mic.Id, mic.Name);
                    try { return CaptureStream.OpenDevice(device, mic.Name, false); }
                    finally { Marshal.ReleaseComObject(device); }
                });
            }

            public float[] Last(double seconds)
            {
                lock (gate)
                {
                    int n = Math.Min(ring.Fill, (int)(seconds * Format.Rate));
                    var result = new float[n * 2];
                    long start = ring.Written - n;
                    for (int i = 0; i < n; i++)
                    {
                        result[i * 2] = ring[start + i, 0];
                        result[i * 2 + 1] = ring[start + i, 1];
                    }
                    return result;
                }
            }

            public void Dispose()
            {
                stop = true;
                thread.Join(2000);
            }
        }

        private static Process Tone(string exe, double freq, double amp, string extra)
        {
            string args = "--freq=" + freq.ToString(CultureInfo.InvariantCulture) + " --amp=" + amp.ToString(CultureInfo.InvariantCulture)
                + " --device=\"" + QuietDevice + "\" --seconds=600" + extra;
            Process p = StartUnderExplorer(Path.Combine(bin, exe), args);
            started.Add(p);
            Pump(0.6);
            return p;
        }

        /// <summary>
        /// Starts a stand-in app the way a normal app runs (its parent is Explorer, not this test), so recording
        /// this process's own audio doesn't also pick up the stand-in's tone.
        /// </summary>
        private static Process StartUnderExplorer(string exe, string args)
        {
            Process explorer = Process.GetProcessesByName("explorer").First();
            IntPtr parent = OpenProcess(0x0080, false, explorer.Id);   // PROCESS_CREATE_PROCESS
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            IntPtr list = Marshal.AllocHGlobal(size);
            IntPtr parentValue = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                InitializeProcThreadAttributeList(list, 1, 0, ref size);
                Marshal.WriteIntPtr(parentValue, parent);
                UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020000, parentValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);
                var info = new STARTUPINFOEX();
                info.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                info.lpAttributeList = list;
                PROCESS_INFORMATION pi;
                string command = "\"" + exe + "\" " + args;
                if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x08000000, IntPtr.Zero, bin, ref info, out pi))
                    throw new InvalidOperationException("couldn't start " + exe + " (error " + Marshal.GetLastWin32Error() + ")");
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return Process.GetProcessById(pi.dwProcessId);
            }
            finally
            {
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
                Marshal.FreeHGlobal(parentValue);
                CloseHandle(parent);
            }
        }

        /// <summary>Ends a stand-in app and the helper process it may have started (only those).</summary>
        private static void KillProcess(Process p)
        {
            foreach (int child in ChildrenOf(p.Id))
                try { Process.GetProcessById(child).Kill(); } catch (Exception) { }
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
        }

        private static List<int> ChildrenOf(int pid)
        {
            var children = new List<int>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            try
            {
                var e = new PROCESSENTRY32 { dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    if (e.th32ParentProcessID == pid) children.Add(e.th32ProcessID);
            }
            finally
            {
                CloseHandle(snap);
            }
            return children;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public int dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string app, string command, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(int flags, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);

        private static void KillTones()
        {
            foreach (string name in new[] { "TestSpotify", "TestChrome", "ToneApp" })
                foreach (Process p in Process.GetProcessesByName(name))
                    try { p.Kill(); } catch (Exception) { }
        }

        private static float[] Sine(int frames, double freq, float amp)
        {
            var s = new float[frames * 2];
            for (int i = 0; i < frames; i++)
                s[i * 2] = s[i * 2 + 1] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / Format.Rate));
            return s;
        }

        /// <summary>Amplitude of one frequency in the left channel (Goertzel).</summary>
        private static double Amplitude(float[] s, double freq)
        {
            int n = s.Length / 2;
            if (n == 0) return 0;
            double w = 2 * Math.PI * freq / Format.Rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++)
            {
                double v = s[i * 2] + c * s1 - s2;
                s2 = s1;
                s1 = v;
            }
            return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - c * s1 * s2)) * 2 / n;
        }

        /// <summary>The strongest amplitude within 3 Hz of freq (speed correction can move a tone a fraction of a hertz).</summary>
        private static double Band(float[] s, double freq)
        {
            double best = 0;
            for (double f = freq - 3; f <= freq + 3; f += 0.25) best = Math.Max(best, Amplitude(s, f));
            return best;
        }

        private static double DominantFrequency(float[] s, double from, double to)
        {
            double best = from, bestA = 0;
            for (double f = from; f <= to; f += 0.25)
            {
                double a = Amplitude(s, f);
                if (a > bestA) { bestA = a; best = f; }
            }
            return best;
        }

        private static double Rms(float[] s)
        {
            double sum = 0;
            for (int i = 0; i < s.Length; i += 2) sum += s[i] * s[i];
            return Math.Sqrt(sum / Math.Max(1, s.Length / 2));
        }

        private static float Peak(float[] s)
        {
            float p = 0;
            foreach (float v in s) p = Math.Max(p, Math.Abs(v));
            return p;
        }

        private static bool Near(double a, double b, double tolerance) { return Math.Abs(a - b) <= tolerance; }

        private static double CpuOver(double seconds)
        {
            TimeSpan before = Process.GetCurrentProcess().TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            Pump(seconds);
            return (Process.GetCurrentProcess().TotalProcessorTime - before).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
        }

        private static void Click(MainWindow w, string name)
        {
            w.Find<WpfControls.Button>(name).RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Primitives.ButtonBase.ClickEvent));
            Pump(0.1);
        }

        private static Rectangle Bounds(MainWindow w)
        {
            RECT r;
            GetWindowRect(new Wpf.Interop.WindowInteropHelper(w.Window).Handle, out r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        private static void Snap(MainWindow w, string path) { SnapRect(Bounds(w), path); }

        private static void SnapRect(Rectangle area, string path)
        {
            using (var bmp = new Bitmap(area.Width, area.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static bool ContentFits(MainWindow w) { return Overflow(w) == "fits"; }

        private static string Overflow(MainWindow w)
        {
            var root = w.Find<Wpf.FrameworkElement>("ContentRoot");
            string name = w.CurrentPage == Page.Settings ? "SettingsPanel" : w.CurrentPage == Page.Setup ? "SetupPanel"
                : w.CurrentPage == Page.Log ? "LogPanel" : "MainPanel";
            var panel = w.Find<Wpf.FrameworkElement>(name);
            double right = root.ActualWidth - 24 + 0.5;
            string problem = null;
            Walk(panel, e =>
            {
                if (problem != null || !e.IsVisible || e.ActualWidth == 0) return;
                Wpf.Rect r = e.TransformToAncestor(root).TransformBounds(new Wpf.Rect(e.RenderSize));
                var text = e as WpfControls.TextBlock;
                string what = text != null ? "'" + text.Text + "'" : e.Name.Length > 0 ? e.Name : e.GetType().Name;
                if (r.Right > right) problem = what + " ends at x=" + r.Right.ToString("0") + " but the page ends at " + right.ToString("0");
                if (text != null && text.TextWrapping == Wpf.TextWrapping.NoWrap && text.TextTrimming == Wpf.TextTrimming.None
                    && TextWidth(text) > text.ActualWidth + 2)
                    problem = what + " is cut off";
            });
            if (problem == null && w.Window.ActualHeight > Wpf.SystemParameters.WorkArea.Height) problem = "the window is taller than the screen";
            return problem ?? "fits";
        }

        private static void Walk(Wpf.DependencyObject e, Action<Wpf.FrameworkElement> visit)
        {
            var element = e as Wpf.FrameworkElement;
            if (element != null) visit(element);
            for (int i = 0; i < WpfMedia.VisualTreeHelper.GetChildrenCount(e); i++) Walk(WpfMedia.VisualTreeHelper.GetChild(e, i), visit);
        }

        private static double TextWidth(WpfControls.TextBlock t)
        {
            var text = new WpfMedia.FormattedText(t.Text, CultureInfo.CurrentUICulture, t.FlowDirection,
                new WpfMedia.Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, WpfMedia.Brushes.Black,
                null, WpfMedia.TextOptions.GetTextFormattingMode(t), 1.0);
            return text.WidthIncludingTrailingWhitespace;
        }

        private static bool HasText(Wpf.DependencyObject e, string text)
        {
            var block = e as WpfControls.TextBlock;
            if (block != null && block.Text == text) return true;
            foreach (object child in Wpf.LogicalTreeHelper.GetChildren(e))
                if (child is Wpf.DependencyObject && HasText((Wpf.DependencyObject)child, text)) return true;
            return false;
        }

        private static bool LogHas(string text) { return File.Exists(Logger.FilePath) && File.ReadAllText(Logger.FilePath).Contains(text); }

        private static void Check(bool ok, string what)
        {
            if (ok) passed++;
            else failed++;
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine(name);
        }

        private static void Pump(double seconds)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                DoEvents();
                Thread.Sleep(10);
            }
        }

        private static bool PumpUntil(Func<bool> done, double seconds)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                if (done()) return true;
                DoEvents();
                Thread.Sleep(10);
            }
            return done();
        }

        private static void DoEvents()
        {
            Forms.Application.DoEvents();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new DispatcherOperationCallback(delegate { frame.Continue = false; return null; }), null);
            Dispatcher.PushFrame(frame);
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    }
}
