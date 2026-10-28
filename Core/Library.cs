using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;
using zSound.Audio;

namespace zSound.Core
{
    /// Eklenen sesler zSound'un kendi klasörüne kopyalanır: orijinal silinse/taşınsa da ses kalır.
    /// Videolardan sadece ses çıkarılır (16-bit WAV, örnek hassas — kırpma noktaları kaymaz).
    public static class Library
    {
        public static string Dir => Path.Combine(Config.Dir, "sounds");

        static readonly string[] Videos = { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".3gp" };
        const double MaxWavMinutes = 20;    // daha uzun videolar olduğu gibi kopyalanır (WAV çok büyür)

        public static bool Owns(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(Dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// kaynağı kütüphaneye alır, kopyanın yolunu döner. Aynı dosya daha önce alındıysa o kopyayı kullanır.
        /// Thread pool'da çağır (büyük dosyada saniyeler sürebilir).
        public static string Import(string src)
        {
            if (Owns(src)) return src;
            var fi = new FileInfo(src);
            string key = Hash($"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
            string name = Safe(Path.GetFileNameWithoutExtension(src));
            string ext = fi.Extension.ToLowerInvariant();
            Directory.CreateDirectory(Dir);

            if (Videos.Contains(ext))
            {
                var wav = Path.Combine(Dir, $"{name}_{key}.wav");
                if (File.Exists(wav)) return wav;
                var tmp = wav + ".tmp";
                try
                {
                    using (var r = Voice.Open(src))
                    {
                        if (r.TotalTime.TotalMinutes <= MaxWavMinutes)
                        {
                            WaveFileWriter.CreateWaveFile(tmp, r.ToSampleProvider().ToWaveProvider16());
                            File.Move(tmp, wav, true);
                            return wav;
                        }
                    }
                }
                catch { try { File.Delete(tmp); } catch { } } // ses çıkarılamadı: dosyayı olduğu gibi kopyala
            }

            var dst = Path.Combine(Dir, $"{name}_{key}{ext}");
            if (!File.Exists(dst))
            {
                var tmp = dst + ".tmp";
                File.Copy(src, tmp, true);
                File.Move(tmp, dst, true);
            }
            return dst;
        }

        /// kütüphanedeki kopyayı sil (çalarken açıksa sessizce geç, açılışta Sweep temizler)
        public static void Delete(string path)
        {
            if (!Owns(path)) return;
            try { File.Delete(path); } catch { }
        }

        /// hiçbir sesin kullanmadığı kopyaları ve yarım kalmış dosyaları sil — açılışta, ekleme başlamadan önce
        public static void Sweep(IEnumerable<string> used)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                var keep = new HashSet<string>(used.Where(Owns).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(Dir))
                    if (!keep.Contains(Path.GetFullPath(f))) try { File.Delete(f); } catch { }
            }
            catch { }
        }

        static string Hash(string s) =>
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)), 0, 4).ToLowerInvariant();

        static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in name) sb.Append(bad.Contains(c) ? '_' : c);
            var r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length > 40) r = r[..40].TrimEnd();
            return r.Length == 0 ? "ses" : r;
        }
    }
}
