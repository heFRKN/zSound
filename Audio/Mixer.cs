using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace zSound.Audio
{
    /// Bir çıkış (hoparlör ya da mikrofon yolu) için karıştırıcı: sesler → kazanç → limiter.
    public sealed class Mixer : ISampleProvider
    {
        readonly object gate = new object();
        Voice[] voices = Array.Empty<Voice>();
        float[] mix = new float[4096];
        readonly int outCh;
        float limEnv;

        public WaveFormat WaveFormat { get; }
        public double Rate { get; }
        public volatile bool SoundsMuted;
        public float Master = 1f;      // seslerin seviyesi
        public bool Limit = true;
        public float Peak;             // UI metresi için
        public long IdleSince = Environment.TickCount64;

        public Mixer(int rate, int channels)
        {
            outCh = Math.Max(1, channels);
            Rate = rate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, outCh);
        }

        public bool HasVoices => voices.Length > 0;

        public bool HasAudible
        {
            get
            {
                foreach (var v in voices) if (!v.Paused && !v.Done) return true;
                return false;
            }
        }

        public void Add(Voice v)
        {
            lock (gate)
            {
                var n = new Voice[voices.Length + 1];
                voices.CopyTo(n, 0);
                n[^1] = v;
                voices = n;
            }
        }

        void RemoveDone()
        {
            lock (gate)
            {
                int keep = 0;
                foreach (var v in voices) if (!v.Done) keep++;
                if (keep == voices.Length) return;
                var n = new Voice[keep];
                int k = 0;
                foreach (var v in voices)
                {
                    if (!v.Done) n[k++] = v;
                    else ThreadPool.UnsafeQueueUserWorkItem(o => ((Voice)o).Dispose(), v);
                }
                voices = n;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / outCh;
            int need = frames * 2;
            if (mix.Length < need) mix = new float[need];
            Array.Clear(mix, 0, need);

            var vs = voices;
            bool done = false;
            float bus = SoundsMuted ? 0f : 1f;
            foreach (var v in vs)
            {
                v.Mix(mix, frames, Rate, bus);
                if (v.Done) done = true;
            }
            if (done) RemoveDone();

            float m = Master;
            if (m != 1f) for (int i = 0; i < need; i++) mix[i] *= m;

            if (vs.Length > 0) IdleSince = Environment.TickCount64;

            // limiter (stereo bağlı, anlık atak, ~120ms bırakma)
            float peak = 0f;
            float rel = (float)Math.Exp(-1.0 / (0.12 * Rate));
            const float th = 0.97f;
            for (int i = 0, j = offset; i < frames; i++)
            {
                float l = mix[2 * i], r = mix[2 * i + 1];
                if (Limit)
                {
                    float a = Math.Max(Math.Abs(l), Math.Abs(r));
                    limEnv = a > limEnv ? a : limEnv * rel;
                    if (limEnv > th) { float g = th / limEnv; l *= g; r *= g; }
                }
                else
                {
                    l = Math.Clamp(l, -1f, 1f);
                    r = Math.Clamp(r, -1f, 1f);
                }
                float p = Math.Max(Math.Abs(l), Math.Abs(r));
                if (p > peak) peak = p;

                if (outCh == 1) buffer[j++] = (l + r) * 0.5f;
                else
                {
                    buffer[j++] = l;
                    buffer[j++] = r;
                    for (int c = 2; c < outCh; c++) buffer[j++] = 0f;
                }
            }
            Peak = Math.Max(peak, Peak * 0.85f);
            return frames * outCh;
        }

        public void StopAll()
        {
            foreach (var v in voices) v.StopRequested = true;
        }

        public void Clear()
        {
            lock (gate)
            {
                foreach (var v in voices) { v.Done = true; Task.Run(v.Dispose); }
                voices = Array.Empty<Voice>();
            }
        }
    }
}
