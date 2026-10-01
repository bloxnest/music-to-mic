using System;

namespace MusicToMic.Audio
{
    internal enum RouteMode { MusicOnly = 0, MicOnly = 1, MicAndMusic = 2 }

    /// <summary>
    /// Mixes the music and the microphone into what Roblox hears. Volumes glide to new values (no clicks
    /// when a slider moves or something is muted), and a limiter keeps the sum from ever clipping.
    /// Settings are written by the window and read by the audio thread, so they're simple volatile fields.
    /// </summary>
    internal sealed class Mixer
    {
        private const float LimitAt = 0.891f;   // -1 dBFS
        private static readonly float Release = (float)(1 - Math.Exp(-1.0 / (0.12 * Format.Rate)));   // ~120 ms recovery
        private static readonly float Glide = (float)(1 - Math.Exp(-1.0 / (0.015 * Format.Rate)));    // ~15 ms volume glide

        public volatile float MusicVolume = 1f, MicVolume = 1f, MasterVolume = 1f;
        public volatile bool MusicMuted, MicMuted, AllMuted;
        public volatile RouteMode Mode = RouteMode.MicAndMusic;

        /// <summary>Push-to-music: false holds the music back (the hotkey isn't held).</summary>
        public volatile bool MusicGate = true;

        private float musicGain, micGain, masterGain, limiter = 1f;

        // meters: the loudest sample since the window last looked (0..1+), and whether the limiter had to act
        private float musicPeak, micPeak, outPeak;
        private bool limited;
        private readonly object meterLock = new object();

        public float MusicTarget
        {
            get { return Mode != RouteMode.MicOnly && !MusicMuted && !AllMuted && MusicGate ? MusicVolume : 0f; }
        }

        public float MicTarget
        {
            get { return Mode != RouteMode.MusicOnly && !MicMuted && !AllMuted ? MicVolume : 0f; }
        }

        /// <summary>Mixes frames of music and mic (stereo, either may be null) into output.</summary>
        public void Mix(float[] music, float[] mic, float[] output, int frames)
        {
            float musicT = MusicTarget, micT = MicTarget, masterT = AllMuted ? 0f : MasterVolume;
            float musicV = MusicVolume, micV = MicVolume;
            float mp = 0, cp = 0, op = 0;
            bool hit = false;
            for (int i = 0; i < frames; i++)
            {
                musicGain += (musicT - musicGain) * Glide;
                micGain += (micT - micGain) * Glide;
                masterGain += (masterT - masterGain) * Glide;
                float l = 0, r = 0;
                if (music != null)
                {
                    float ml = music[i * 2], mr = music[i * 2 + 1];
                    mp = Math.Max(mp, Math.Max(Math.Abs(ml), Math.Abs(mr)) * musicV);
                    l += ml * musicGain;
                    r += mr * musicGain;
                }
                if (mic != null)
                {
                    float cl = mic[i * 2], cr = mic[i * 2 + 1];
                    cp = Math.Max(cp, Math.Max(Math.Abs(cl), Math.Abs(cr)) * micV);
                    l += cl * micGain;
                    r += cr * micGain;
                }
                l *= masterGain;
                r *= masterGain;

                // limiter: turn down instantly if the sum would go over -1 dBFS, then recover slowly
                float peak = Math.Max(Math.Abs(l), Math.Abs(r));
                if (peak * limiter > LimitAt)
                {
                    limiter = LimitAt / peak;
                    if (peak > 1f) hit = true;   // without the limiter this would have clipped
                }
                else limiter += (1f - limiter) * Release;
                l *= limiter;
                r *= limiter;
                output[i * 2] = l < -1f ? -1f : l > 1f ? 1f : l;
                output[i * 2 + 1] = r < -1f ? -1f : r > 1f ? 1f : r;
                op = Math.Max(op, Math.Max(Math.Abs(l), Math.Abs(r)));
            }
            lock (meterLock)
            {
                musicPeak = Math.Max(musicPeak, mp);
                micPeak = Math.Max(micPeak, cp);
                outPeak = Math.Max(outPeak, op);
                limited |= hit;
            }
        }

        /// <summary>For the meters: the peaks since the last call (then starts over).</summary>
        public void TakePeaks(out float music, out float mic, out float output, out bool clipped)
        {
            lock (meterLock)
            {
                music = musicPeak;
                mic = micPeak;
                output = outPeak;
                clipped = limited;
                musicPeak = micPeak = outPeak = 0;
                limited = false;
            }
        }
    }
}
