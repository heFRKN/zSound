using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using zSound.Core;

namespace zSound.Audio;

/// End-to-end verification. Only summary metrics are saved, never microphone audio.
public static class MicProbe
{
    public static string Run(string deviceId)
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDevice(deviceId);
        using var capture = new WasapiCapture(dev);
        capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var gate = new object();
        var samples = new List<float>();
        Exception error = null;
        capture.DataAvailable += (_, e) =>
        {
            lock (gate)
                for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
                    samples.Add(BitConverter.ToSingle(e.Buffer, i));
        };
        capture.RecordingStopped += (_, e) => error = e.Exception;
        capture.StartRecording();
        Thread.Sleep(1000);
        float[] baseline;
        lock (gate) { baseline = samples.ToArray(); samples.Clear(); }
        if (error != null) throw error;
        if (baseline.Length < 12000) throw new Exception("Mikrofondan veri alınamadı.");
        using var bus = new MicBus();
        var path = Path.Combine(Path.GetTempPath(), "zsound-probe-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // Two simultaneous frequencies distinguish injection from ambient speech.
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                var tone = new float[48000 * 3 * 2];
                for (int i = 0; i < tone.Length / 2; i++)
                    tone[i * 2] = tone[i * 2 + 1] = (float)(0.12 * (Math.Sin(2 * Math.PI * 997 * i / 48000) + Math.Sin(2 * Math.PI * 1499 * i / 48000)));
                writer.WriteSamples(tone, 0, tone.Length);
            }
            Thread.Sleep(1000);
            bus.Mixer.Add(new Voice(new Sound { Path = path }));
            bus.Wake();
            Thread.Sleep(500);
            lock (gate) samples.Clear();
            Thread.Sleep(1800);
            float[] received;
            lock (gate) received = samples.ToArray();
            var a = Tone(received, 997); var b = Tone(received, 1499);
            var report = $"device={dev.FriendlyName}; frames={received.Length}; readers={bus.Readers}; tone997={a:F6}; tone1499={b:F6}; baseline997={Tone(baseline, 997):F6}; baseline1499={Tone(baseline, 1499):F6}";
            if (error != null) throw error;
            if (bus.Readers < 1 || received.Length < 48000 || a < 0.025 || b < 0.025 || a < Tone(baseline, 997) * 4 || b < Tone(baseline, 1499) * 4)
                throw new Exception("Mikrofon ses aktarım testi başarısız: " + report);
            bus.Mixer.StopAll();
            bus.Wake();
            Thread.Sleep(500);
            lock (gate) samples.Clear();
            Thread.Sleep(600);
            lock (gate)
                if (samples.Count < 12000) throw new Exception("Test sonrası mikrofon akışı durdu.");
            return "PASS " + report;
        }
        finally
        {
            capture.StopRecording();
            bus.Mixer.Clear();
            try { File.Delete(path); } catch { }
        }
    }

    static double Tone(float[] data, double hz)
    {
        // Short windows tolerate the independent USB capture clock.
        const int n = 4800;
        if (data.Length < n) return 0;
        double total = 0; int windows = 0;
        for (int offset = 0; offset + n <= data.Length; offset += n)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double phase = 2 * Math.PI * hz * i / 48000;
                re += data[offset + i] * Math.Cos(phase);
                im += data[offset + i] * Math.Sin(phase);
            }
            total += 2 * Math.Sqrt(re * re + im * im) / n;
            windows++;
        }
        return total / windows;
    }
}
