using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using zSound.Core;

namespace zSound.Audio
{
    public sealed class DeviceItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    /// Bir çıkış cihazı + karıştırıcısı. Boşta kalınca duraklar (CPU 0, uyku engellenmez).
    sealed class Output : IDisposable
    {
        public readonly Mixer Mixer;
        public readonly string Name;
        readonly WasapiOut wo;
        public bool Running;
        public string Error;

        public Output(MMDevice dev, int latency)
        {
            Name = dev.FriendlyName;
            var mf = dev.AudioClient.MixFormat;
            Mixer = new Mixer(mf.SampleRate, mf.Channels);
            wo = new WasapiOut(dev, AudioClientShareMode.Shared, true, latency);
            wo.Init(Mixer);
            wo.PlaybackStopped += (s, e) => { Running = false; if (e.Exception != null) Error = e.Exception.Message; };
        }

        public void Wake()
        {
            if (Running) return;
            try { wo.Play(); Running = true; } catch (Exception e) { Error = e.Message; }
        }

        public void Sleep()
        {
            if (!Running) return;
            try { wo.Pause(); } catch { }
            Running = false;
        }

        public void Dispose()
        {
            try { wo.Stop(); } catch { }
            try { wo.Dispose(); } catch { }
            Mixer.Clear();
        }
    }

    /// Aynı sesin mikrofon ve hoparlör kopyaları
    public sealed class Playback
    {
        public Sound Sound;
        public Voice A, B;
        public bool Stopping;
        Voice Any => A ?? B;
        public bool Done => (A == null || A.Done) && (B == null || B.Done);
        public bool Paused => Any?.Paused ?? false;
        public double Position => Any?.PositionSec ?? 0;
        public double Start => Any?.StartSec ?? 0;
        public double End => Any?.EndSec ?? 0;

        public void Stop() { Stopping = true; if (A != null) A.StopRequested = true; if (B != null) B.StopRequested = true; }
        public void SetPaused(bool p) { if (A != null) A.Paused = p; if (B != null) B.Paused = p; }
    }

    public sealed class Engine : IDisposable
    {
        public static volatile bool NormalizeOn;

        readonly MicBus bus = new MicBus();
        Output monitor;
        Settings s;
        readonly List<Playback> plays = new List<Playback>();
        bool pttDown;

        public string MonitorName => monitor?.Name;
        public string LastError { get; private set; }
        public IReadOnlyList<Playback> Plays => plays;
        public float MicPeak => bus.Mixer.Peak;
        /// şu an mikrofonu işleyen eklenti sayısı (0 = eklenti yüklenmedi ya da mikrofonu kullanan yok)
        public int MicReaders => bus.Readers;
        public bool MicMuted { get; private set; }

        public event Action Changed;

        public static List<DeviceItem> Devices(DataFlow flow, string defaultLabel)
        {
            var list = new List<DeviceItem> { new DeviceItem { Id = "", Name = defaultLabel } };
            try
            {
                using var en = new MMDeviceEnumerator();
                // gerçek cihazlar (JIN, hoparlör…) üstte, sanal olanlar (Sonar, kablo…) altta
                list.AddRange(en.EnumerateAudioEndPoints(flow, DeviceState.Active)
                    .Select(d => new DeviceItem { Id = d.ID, Name = d.FriendlyName })
                    .OrderBy(d => IsVirtual(d.Name)).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase));
            }
            catch { }
            return list;
        }

        static bool IsVirtual(string name) =>
            new[] { "Sonar", "CABLE", "VB-Audio", "Voicemeeter", "Virtual", "Voicemod", "NVIDIA Broadcast" }
                .Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        /// ayarlardaki mikrofon (boşsa Windows varsayılanı) → (id, isim)
        public static (string id, string name) ResolveMic(string id)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                if (!string.IsNullOrEmpty(id))
                {
                    try { var d = en.GetDevice(id); if (d.State == DeviceState.Active) return (d.ID, d.FriendlyName); } catch { }
                }
                var def = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                return (def.ID, def.FriendlyName);
            }
            catch { return (null, null); }
        }

        public void Apply(Settings st, bool rebuild)
        {
            s = st;
            NormalizeOn = st.Normalize;
            if (rebuild) Build();
            var m = bus.Mixer;
            m.Master = st.MicVolume * st.Boost;
            m.Limit = st.Limiter;
            m.SoundsMuted = !st.SendToMic;
            bus.SendOn = st.SendToMic;
            bus.MicGain = st.MicGain;
            bus.DuckLevel = st.DuckMic ? st.DuckLevel : 1f;
            bus.MicMuted = MicMuted;
            bus.LeadMs = st.LatencyMs;
            if (monitor != null)
            {
                monitor.Mixer.Master = st.MonitorVolume;
                monitor.Mixer.Limit = st.Limiter;
                monitor.Mixer.SoundsMuted = !st.SendToSpeakers;
            }
            Changed?.Invoke();
        }

        public void SetMicMuted(bool m)
        {
            MicMuted = m;
            bus.MicMuted = m;
            Changed?.Invoke();
        }

        void Build()
        {
            StopAllNow();
            monitor?.Dispose();
            monitor = null;
            LastError = null;
            try
            {
                using var en = new MMDeviceEnumerator();
                MMDevice md = null;
                if (!string.IsNullOrEmpty(s.MonitorDevice))
                {
                    try { var d = en.GetDevice(s.MonitorDevice); if (d.State == DeviceState.Active) md = d; } catch { }
                }
                try { md ??= en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
                if (md != null) try { monitor = new Output(md, s.LatencyMs); } catch (Exception e) { LastError = "Hoparlör: " + e.Message; }
            }
            catch (Exception e) { LastError = e.Message; }
        }

        /// UI zamanlayıcısından çağrılır. true dönerse hâlâ yapacak iş var.
        public bool Tick()
        {
            bool removed = plays.RemoveAll(p => p.Done) > 0;
            bool audibleOnMic = bus.Mixer.HasAudible && s.SendToMic;
            if (s.PushToTalkVk > 0 && audibleOnMic != pttDown)
            {
                pttDown = audibleOnMic;
                Native.SendKey(s.PushToTalkVk, pttDown);
            }
            long now = Environment.TickCount64;
            if (monitor != null && monitor.Running)
            {
                if (monitor.Error != null) LastError = monitor.Error;
                if (!monitor.Mixer.HasVoices && now - monitor.Mixer.IdleSince > 3000) monitor.Sleep();
            }
            if (removed) Changed?.Invoke();
            return plays.Count > 0 || (monitor?.Running ?? false);
        }

        public async Task<Playback> Play(Sound snd)
        {
            if (s.Overlap == OverlapMode.StopOthers) StopAll();
            else if (s.Overlap == OverlapMode.Restart)
                foreach (var p in plays) if (p.Sound == snd) p.Stop();

            bool toMic = snd.Target != OutputTarget.SpeakersOnly;
            bool toMon = monitor != null && snd.Target != OutputTarget.MicOnly;
            if (!toMic && !toMon) { LastError = "Çıkış cihazı yok"; Changed?.Invoke(); return null; }

            var pb = new Playback { Sound = snd };
            try
            {
                // dosya açma/sarma işi thread pool'da (MTA) — UI ve ses thread'i takılmaz
                await Task.Run(() =>
                {
                    if (toMic) pb.A = new Voice(snd);
                    if (toMon) pb.B = new Voice(snd);
                });
            }
            catch (Exception e)
            {
                pb.A?.Dispose(); pb.B?.Dispose();
                LastError = "Açılamadı: " + System.IO.Path.GetFileName(snd.Path) + " (" + e.Message + ")";
                Changed?.Invoke();
                return null;
            }
            if (pb.A != null) { bus.Mixer.Add(pb.A); bus.Wake(); }
            if (pb.B != null) { monitor.Mixer.Add(pb.B); monitor.Wake(); }
            plays.Add(pb);
            snd.PlayCount++;
            LastError = null;
            Changed?.Invoke();
            return pb;
        }

        public void Stop(Sound snd)
        {
            foreach (var p in plays) if (p.Sound == snd) p.Stop();
        }

        public bool IsPlaying(Sound snd) => plays.Any(p => p.Sound == snd && !p.Stopping && !p.Done);

        public void StopAll()
        {
            foreach (var p in plays) p.Stop();
            Changed?.Invoke();
        }

        void StopAllNow()
        {
            foreach (var p in plays) p.Stop();
            plays.Clear();
        }

        /// hepsi duraklatılmışsa devam ettirir, değilse duraklatır
        public void TogglePause()
        {
            bool anyRunning = plays.Any(p => !p.Paused);
            foreach (var p in plays) p.SetPaused(anyRunning);
            Changed?.Invoke();
        }

        public void Dispose()
        {
            if (pttDown && s != null) Native.SendKey(s.PushToTalkVk, false);
            bus.Dispose();
            monitor?.Dispose();
        }

        /// dalga formu: her parça için tepe değer (0..1)
        public static float[] Peaks(string path, int buckets)
        {
            using var st = Voice.Open(path);
            var sp = st.ToSampleProvider();
            int ch = sp.WaveFormat.Channels;
            long frames = Math.Max(1, (long)(st.TotalTime.TotalSeconds * sp.WaveFormat.SampleRate));
            var peaks = new float[buckets];
            var buf = new float[16384 * ch];
            long f = 0;
            int r;
            while ((r = sp.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < r; i += ch, f++)
                {
                    int b = (int)Math.Min(buckets - 1, f * buckets / frames);
                    float a = Math.Abs(buf[i]);
                    if (a > peaks[b]) peaks[b] = a;
                }
            }
            float max = 0;
            foreach (var p in peaks) if (p > max) max = p;
            if (max > 0) for (int i = 0; i < buckets; i++) peaks[i] /= max;
            return peaks;
        }

        /// süre + (istenirse) ses yüksekliği analizi — arka planda
        public static (double dur, float gain) Analyze(string path, bool loudness)
        {
            using var st = Voice.Open(path);
            double dur = st.TotalTime.TotalSeconds;
            if (!loudness) return (dur, 1f);
            var sp = st.ToSampleProvider();
            var buf = new float[8192 * sp.WaveFormat.Channels];
            double sum = 0; long n = 0; float peak = 0;
            int r;
            while ((r = sp.Read(buf, 0, buf.Length)) > 0)
            {
                double bs = 0;
                for (int i = 0; i < r; i++) { float v = buf[i]; bs += v * v; float a = Math.Abs(v); if (a > peak) peak = a; }
                // sessiz blokları sayma
                if (bs / r > 1e-6) { sum += bs; n += r; }
            }
            if (n == 0 || peak <= 0) return (dur, 1f);
            double rms = Math.Sqrt(sum / n);
            double target = Math.Pow(10, -16 / 20.0);
            double g = target / rms;
            g = Math.Min(g, 1.6 / peak); // limiter halleder ama çok da zorlamayalım
            return (dur, (float)Math.Clamp(g, 0.1, 4.0));
        }
    }
}
