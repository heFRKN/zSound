using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace zSound.Core
{
    public enum OverlapMode { Overlap, Restart, StopOthers }
    public enum OutputTarget { Both, MicOnly, SpeakersOnly }

    public sealed class Hotkey
    {
        public int Vk { get; set; }
        public int Vk2 { get; set; }   // kombinasyonun diğer tuşu (Num1+Num2 gibi), 0 = yok
        public bool Ctrl { get; set; }
        public bool Alt { get; set; }
        public bool Shift { get; set; }
        public bool Win { get; set; }

        [JsonIgnore] public bool IsEmpty => Vk == 0;
        [JsonIgnore] public bool IsChord => Vk2 != 0;

        /// kombinasyonda sıra önemsiz: Num1+Num2 == Num2+Num1
        public bool Same(Hotkey o) => o != null && o.Ctrl == Ctrl && o.Alt == Alt && o.Shift == Shift && o.Win == Win &&
                                      ((o.Vk == Vk && o.Vk2 == Vk2) || (o.Vk == Vk2 && o.Vk2 == Vk));

        public bool Uses(int vk) => Vk == vk || Vk2 == vk;

        public Hotkey Single() => new Hotkey { Vk = Vk, Ctrl = Ctrl, Alt = Alt, Shift = Shift, Win = Win };

        public override string ToString()
        {
            if (Vk == 0) return "";
            var s = "";
            if (Ctrl) s += "Ctrl+";
            if (Alt) s += "Alt+";
            if (Shift) s += "Shift+";
            if (Win) s += "Win+";
            if (Vk2 != 0) s += KeyNames.Name(Vk2) + "+";
            return s + KeyNames.Name(Vk);
        }
    }

    public sealed class Sound
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Path { get; set; }             // zSound'un kendi kopyası (Library)
        public string Source { get; set; }           // eklendiği orijinal dosya (bilgi için)
        public string Name { get; set; }
        public double DurationSec { get; set; }
        public Hotkey Key { get; set; }
        public float Volume { get; set; } = 1f;      // 0..2
        public float Speed { get; set; } = 1f;       // 0.5..2 (pitch ile)
        public double StartSec { get; set; }
        public double EndSec { get; set; }           // 0 = sonuna kadar
        public double FadeInSec { get; set; }
        public double FadeOutSec { get; set; }
        public bool Loop { get; set; }
        public bool HoldToPlay { get; set; }
        public bool Favorite { get; set; }
        public OutputTarget Target { get; set; } = OutputTarget.Both;
        public float Gain { get; set; } = 1f;        // normalize çarpanı (hesaplanır)
        public bool Measured { get; set; }
        public int PlayCount { get; set; }
    }

    public sealed class Settings
    {
        public string MonitorDevice { get; set; }    // senin hoparlörün/kulaklığın
        public string MicDevice { get; set; }        // mikrofon eklentisinin kurulacağı gerçek mikrofon (boş = varsayılan)

        public bool SendToMic { get; set; } = true;
        public bool SendToSpeakers { get; set; } = true;
        public float MicVolume { get; set; } = 0.8f;      // seslerin mikrofona giden seviyesi
        public float MonitorVolume { get; set; } = 0.5f;  // senin duyduğun seviye

        public float MicGain { get; set; } = 1f;
        public bool DuckMic { get; set; } = true;
        public float DuckLevel { get; set; } = 0.25f;     // ses çalarken mikrofon oranı

        public float Boost { get; set; } = 1f;            // 1..4 (çıkışa kazanç)
        public bool Limiter { get; set; } = true;
        public bool Normalize { get; set; }
        public OverlapMode Overlap { get; set; } = OverlapMode.Restart;
        public bool PressAgainStops { get; set; }
        public int LatencyMs { get; set; } = 40;

        public int PushToTalkVk { get; set; }             // 0 = kapalı
        public Hotkey KeyStopAll { get; set; }
        public Hotkey KeyPauseAll { get; set; }
        public Hotkey KeyRandom { get; set; }
        public Hotkey KeyToggleMic { get; set; }
        public Hotkey KeyToggleSpeakers { get; set; }
        public Hotkey KeyToggleMicPass { get; set; }
        public Hotkey KeyReplayLast { get; set; }

        public bool MinimizeToTray { get; set; } = true;
        public bool StartMinimized { get; set; }
        public bool StartWithWindows { get; set; }
        public bool GpuRender { get; set; }
        public bool OnlyFavorites { get; set; }
        public bool Compact { get; set; }
        public string Language { get; set; }              // "en" / "tr"; boş = Windows diline göre
        public bool AuditionOnRelease { get; set; } = true; // düzenleyicide kenarı bırakınca kesilen yeri çal
        public double WinW { get; set; } = 460;
        public double WinH { get; set; } = 640;
        public double WinX { get; set; } = double.NaN;
        public double WinY { get; set; } = double.NaN;
    }

    public sealed class Config
    {
        public Settings Settings { get; set; } = new Settings();
        public List<Sound> Sounds { get; set; } = new List<Sound>();

        public static string Dir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "zSound");
        static string FilePath => System.IO.Path.Combine(Dir, "config.json");

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Opts) ?? new Config();
            }
            catch { }
            return new Config();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
                File.Move(tmp, FilePath, true);
            }
            catch { }
        }
    }

    public static class KeyNames
    {
        public static string Name(int vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            switch (vk)
            {
                case 0x01: return "Mouse1";
                case 0x02: return "Mouse2";
                case 0x04: return "Mouse3";
                case 0x05: return "Mouse4";
                case 0x06: return "Mouse5";
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x13: return "Pause";
                case 0x14: return "CapsLock";
                case 0x1B: return "Esc";
                case 0x20: return "Space";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2C: return "PrtSc";
                case 0x2D: return "Ins";
                case 0x2E: return "Del";
                case 0x6A: return "Num*";
                case 0x6B: return "Num+";
                case 0x6D: return "Num-";
                case 0x6E: return "Num.";
                case 0x6F: return "Num/";
                case 0x90: return "NumLock";
                case 0x91: return "ScrollLock";
                case 0xA0: case 0xA1: case 0x10: return "Shift";
                case 0xA2: case 0xA3: case 0x11: return "Ctrl";
                case 0xA4: case 0xA5: case 0x12: return "Alt";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
                case 0xE2: return "<";
                case 0xB0: return "Media▶▶";
                case 0xB1: return "Media◀◀";
                case 0xB2: return "MediaStop";
                case 0xB3: return "MediaPlay";
            }
            return "0x" + vk.ToString("X2");
        }
    }
}
