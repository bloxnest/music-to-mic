using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MusicToMic.Audio;

namespace MusicToMic.Tests
{
    /// <summary>
    /// A stand-in music app for the self-tests: plays a quiet sine tone on a chosen device.
    ///   --freq=440 --amp=0.05 --device=Digital --seconds=60
    ///   --child   start a copy of itself that plays, and stay silent (like Chrome's audio helper process)
    ///   --beeps   short tone bursts instead of a steady tone (for measuring delay)
    /// </summary>
    internal static class ToneApp
    {
        [MTAThread]
        private static int Main(string[] args)
        {
            var opts = new Dictionary<string, string>();
            foreach (string a in args)
            {
                string[] kv = a.TrimStart('-').Split(new[] { '=' }, 2);
                opts[kv[0]] = kv.Length > 1 ? kv[1] : "1";
            }
            double seconds = Get(opts, "seconds", 60);
            if (opts.ContainsKey("child"))
            {
                var copy = new List<string>();
                foreach (string a in args) if (a != "--child") copy.Add("\"" + a + "\"");
                var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, string.Join(" ", copy));
                start.UseShellExecute = false;
                Process child = Process.Start(start);
                Console.WriteLine("child " + child.Id);
                child.WaitForExit((int)(seconds * 1000) + 2000);
                if (!child.HasExited) child.Kill();
                return 0;
            }

            double freq = Get(opts, "freq", 440), amp = Get(opts, "amp", 0.05);
            bool beeps = opts.ContainsKey("beeps");
            string want = opts.ContainsKey("device") ? opts["device"] : null;
            IMMDevice device = null;
            string name = "default output";
            foreach (AudioDevice d in Devices.List(DataFlow.Render))
                if (want != null && d.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    device = Devices.Get(d.Id, d.Name);
                    name = d.Name;
                    break;
                }
            if (device == null) device = Devices.Default(DataFlow.Render);
            Console.WriteLine("playing " + freq + " Hz on " + name);

            using (var stream = new RenderStream(device, name, null, 100))
            {
                var block = new float[stream.BufferFrames * 2];
                long n = 0;
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    int space = stream.BufferFrames - stream.Queued;
                    int frames = Math.Min(space, Format.Rate / 20);   // keep about 50 ms queued
                    frames = Math.Max(0, Math.Min(frames, Format.Rate / 20 - stream.Queued));
                    for (int i = 0; i < frames; i++, n++)
                    {
                        // beeps: 50 ms on, 450 ms off
                        bool on = !beeps || (n % (Format.Rate / 2)) < Format.Rate / 20;
                        float s = on ? (float)(amp * Math.Sin(2 * Math.PI * freq * n / Format.Rate)) : 0f;
                        block[i * 2] = s;
                        block[i * 2 + 1] = s;
                    }
                    stream.Write(block, frames);
                    Thread.Sleep(10);
                }
            }
            return 0;
        }

        private static double Get(Dictionary<string, string> opts, string key, double fallback)
        {
            string v;
            return opts.TryGetValue(key, out v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        }
    }
}
