using System;
using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using zSound.Core;

namespace zSound.Audio
{
    /// Tek bir çalan ses (bir çıkış için). Kaynağı stream eder, hızı/örnekleme hızını
    /// lineer interpolasyonla çevirir, ses/fade/trim/loop uygular. Render thread'inde çalışır.
    public sealed class Voice : IDisposable
    {
        readonly WaveStream stream;
        readonly ISampleProvider sp;
        readonly int ch, srcRate;
        readonly Sound snd;
        readonly long startFrame, endFrame;
        readonly float[] rd;
        int rdLen, rdPos;
        float aL, aR, bL, bR;
        double frac;
        long pos;            // kaynaktaki mutlak frame
        long played;         // toplam çalınan kaynak frame (fade-in için)
        float stopGain = 1f;
        bool primed;

        public volatile bool Paused;
        public volatile bool StopRequested;
        public volatile bool Done;
        public Sound Sound => snd;
        public double PositionSec => (double)pos / srcRate;
        public double StartSec => (double)startFrame / srcRate;
        public double EndSec => (double)endFrame / srcRate;

        public static WaveStream Open(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ogg" || ext == ".oga")
                return new VorbisWaveReader(path);
            if (ext == ".wav")
            {
                try
                {
                    var w = new WaveFileReader(path);
                    var t = w.WaveFormat.Encoding;
                    if (t == WaveFormatEncoding.Pcm || t == WaveFormatEncoding.IeeeFloat || t == WaveFormatEncoding.Extensible) return w;
                    w.Dispose();
                }
                catch { }
            }
            return new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
            {
                RequestFloatOutput = true,
                SingleReaderObject = true,
                RepositionInRead = true
            });
        }

        /// MTA (thread pool) üzerinde çağır: dosyayı açar, başa sarar, ilk tamponu doldurur.
        public Voice(Sound snd)
        {
            this.snd = snd;
            stream = Open(snd.Path);
            sp = stream.ToSampleProvider();
            ch = sp.WaveFormat.Channels;
            srcRate = sp.WaveFormat.SampleRate;
            long total = (long)(stream.TotalTime.TotalSeconds * srcRate);
            startFrame = (long)(Math.Max(0, snd.StartSec) * srcRate);
            endFrame = snd.EndSec > 0 ? Math.Min(total, (long)(snd.EndSec * srcRate)) : total;
            if (endFrame <= startFrame) endFrame = total;
            if (startFrame >= endFrame) startFrame = 0;
            rd = new float[2048 * ch];
            Seek();
            Fill();
        }

        void Seek()
        {
            if (startFrame > 0 || pos != 0)
                stream.CurrentTime = TimeSpan.FromSeconds((double)startFrame / srcRate);
            pos = startFrame;
            rdLen = rdPos = 0;
        }

        bool Fill()
        {
            int n = sp.Read(rd, 0, rd.Length);
            rdLen = n - n % ch;
            rdPos = 0;
            return rdLen > 0;
        }

        bool Next(out float l, out float r)
        {
            for (int tries = 0; tries < 2; tries++)
            {
                bool atEnd = endFrame > 0 && pos >= endFrame;
                if (!atEnd && (rdPos < rdLen || Fill()))
                {
                    l = rd[rdPos];
                    r = ch > 1 ? rd[rdPos + 1] : l;
                    rdPos += ch;
                    pos++;
                    played++;
                    return true;
                }
                if (!snd.Loop || StopRequested) break;
                try { Seek(); } catch { break; }
            }
            l = r = 0;
            return false;
        }

        /// stereo karışım tamponuna ekler
        public void Mix(float[] mix, int frames, double dstRate, float bus)
        {
            if (Done || Paused) return;
            if (!primed)
            {
                if (!Next(out aL, out aR) || !Next(out bL, out bR)) { Done = true; return; }
                primed = true;
            }
            double step = srcRate * (double)Math.Clamp(snd.Speed, 0.25f, 3f) / dstRate;
            float vol = snd.Volume * (Engine.NormalizeOn ? snd.Gain : 1f) * bus;
            double fadeIn = snd.FadeInSec * srcRate, fadeOut = snd.FadeOutSec * srcRate;
            float stopStep = (float)(1.0 / (0.025 * dstRate));
            bool ended = false;

            for (int i = 0, j = 0; i < frames; i++, j += 2)
            {
                float g = vol;
                if (fadeIn > 0 && played < fadeIn) g *= (float)(played / fadeIn);
                if (fadeOut > 0 && !snd.Loop)
                {
                    double rem = endFrame - pos;
                    if (rem < fadeOut) g *= (float)Math.Max(0, rem / fadeOut);
                }
                if (StopRequested)
                {
                    stopGain -= stopStep;
                    if (stopGain <= 0) { Done = true; return; }
                    g *= stopGain;
                }

                float f = (float)frac;
                mix[j] += (aL + (bL - aL) * f) * g;
                mix[j + 1] += (aR + (bR - aR) * f) * g;

                frac += step;
                while (frac >= 1.0)
                {
                    frac -= 1.0;
                    aL = bL; aR = bR;
                    if (!Next(out bL, out bR)) { ended = true; break; }
                }
                if (ended) break;
            }
            if (ended) Done = true;
        }

        public void Dispose()
        {
            try { stream.Dispose(); } catch { }
        }
    }
}
