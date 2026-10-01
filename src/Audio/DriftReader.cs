using System;

namespace MusicToMic.Audio
{
    /// <summary>
    /// Reads one input (the music or the mic) at the output's pace. Every audio device runs on its own clock,
    /// so over hours they drift apart by a fraction of a percent. To stay in sync without gaps or a growing
    /// delay, this keeps the input's buffer at a steady level by reading it a tiny bit faster or slower
    /// (at most 0.5%, inaudible). Reading between samples uses a windowed-sinc filter, so even cymbals and
    /// hi-hats stay clean. At exactly 1.0 it's a plain copy.
    /// </summary>
    internal sealed class DriftReader
    {
        private const double MaxCorrection = 0.005;
        // per second of buffer error: gentle (5 ms off = 0.05% speed, about 1 cent of pitch) and well damped
        private const double Kp = 0.1, Ki = 0.004;

        // the in-between filter: 32 taps (16 each side), 512 positions between two samples
        private const int Half = 16, Taps = Half * 2, Phases = 512;
        private static readonly float[] Kernel = BuildKernel();

        private readonly FloatRing ring;
        private double position;      // read position in frames (fractional)
        private double smoothedFill, integral, ratio = 1;
        private bool primed;

        /// <summary>How much input to keep buffered, in frames: enough to ride out packets arriving unevenly.</summary>
        public int TargetFrames;
        public int Underruns, Overruns;

        /// <summary>Self-tests: read at exactly this speed instead of following the buffer (0 = normal).</summary>
        internal double FixedRatio;

        public DriftReader(FloatRing ring, int targetFrames)
        {
            this.ring = ring;
            TargetFrames = targetFrames;
            position = ring.Read + Half;
        }

        public double Ratio { get { return ratio; } }
        public bool Playing { get { return primed; } }
        public double FillFrames { get { return ring.Written - position; } }
        internal double Position { get { return position; } }

        public void Reset()
        {
            ring.Clear();
            position = ring.Read + Half;
            primed = false;
            integral = 0;
            ratio = 1;
        }

        /// <summary>Fills dst (stereo) with frames of input. Returns false (and silence) while there's nothing to play.</summary>
        public bool Read(float[] dst, int frames)
        {
            if (position < ring.Read + Half) position = ring.Read + Half;   // the ring dropped old audio (overflow)
            double fill = ring.Written - position;
            double wanted = TargetFrames + frames;

            if (!primed)
            {
                // (re)start only once a comfortable amount has arrived, so it doesn't stutter
                if (fill < wanted + Half)
                {
                    Array.Clear(dst, 0, frames * 2);
                    return false;
                }
                // start exactly at the target delay (a packet may have brought a few ms extra)
                position = ring.Written - wanted;
                fill = wanted;
                primed = true;
                smoothedFill = fill;
                integral = 0;
            }
            if (fill > wanted * 4 + Format.Rate / 10)
            {
                // a long stall piled audio up: skip ahead instead of staying late forever
                position = ring.Written - wanted;
                fill = wanted;
                smoothedFill = fill;
                Overruns++;
            }

            double seconds = (double)frames / Format.Rate;
            smoothedFill += (fill - smoothedFill) * Math.Min(1, seconds * 2);   // ~0.5 s smoothing
            double error = (smoothedFill - wanted) / Format.Rate;               // seconds too much (+) or too little (-)
            integral = Clamp(integral + error * Ki * seconds, -MaxCorrection, MaxCorrection);
            ratio = FixedRatio > 0 ? FixedRatio : 1 + Clamp(error * Kp + integral, -MaxCorrection, MaxCorrection);

            if (fill < frames * ratio + Half + 2)
            {
                // the input stopped (app paused, packets late): go quiet and wait for it to fill up again
                Array.Clear(dst, 0, frames * 2);
                primed = false;
                Underruns++;
                return false;
            }

            double pos = position;
            for (int i = 0; i < frames; i++)
            {
                long index = (long)pos;
                double t = pos - index;
                if (t == 0)
                {
                    dst[i * 2] = ring[index, 0];
                    dst[i * 2 + 1] = ring[index, 1];
                }
                else
                {
                    // the filter for this in-between position (blended between the two nearest table rows)
                    double phase = t * Phases;
                    int p = (int)phase;
                    float blend = (float)(phase - p);
                    int a = p * Taps, b = a + Taps;
                    float l = 0, r = 0;
                    long first = index - (Half - 1);
                    for (int k = 0; k < Taps; k++)
                    {
                        float c = Kernel[a + k] + blend * (Kernel[b + k] - Kernel[a + k]);
                        l += c * ring[first + k, 0];
                        r += c * ring[first + k, 1];
                    }
                    dst[i * 2] = l;
                    dst[i * 2 + 1] = r;
                }
                pos += ratio;
            }
            position = pos;
            ring.Read = Math.Max(ring.Read, (long)position - Half);   // keep the history the filter needs
            return true;
        }

        /// <summary>
        /// Blackman-windowed sinc, low-passed just under 24 kHz, one row per in-between position.
        /// Each row sums to 1, so the level never changes.
        /// </summary>
        private static float[] BuildKernel()
        {
            const double cutoff = 0.91;   // of the Nyquist frequency: passes everything up to ~21 kHz
            var table = new float[(Phases + 1) * Taps];
            var row = new double[Taps];
            for (int p = 0; p <= Phases; p++)
            {
                double t = (double)p / Phases, sum = 0;
                for (int k = 0; k < Taps; k++)
                {
                    double x = k - (Half - 1) - t;   // distance from the wanted point, in samples
                    double sinc = x == 0 ? 1 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                    double w = Math.Abs(x) >= Half ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * x / Half) + 0.08 * Math.Cos(2 * Math.PI * x / Half);
                    row[k] = sinc * w;
                    sum += row[k];
                }
                for (int k = 0; k < Taps; k++) table[p * Taps + k] = (float)(row[k] / sum);
            }
            return table;
        }

        private static double Clamp(double v, double min, double max) { return v < min ? min : v > max ? max : v; }
    }
}
