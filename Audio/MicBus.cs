using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace zSound.Audio
{
    /// Mikrofon eklentisine (native/zsapo.cpp) giden yol: sesleri "zSoundMicBus" halka tamponuna yazar.
    /// Belleği biz kendi oturumumuzda oluştururuz, APO (audiodg) "Session\<n>\" ile açar —
    /// audiodg Global\ nesne oluşturamaz (Audiosrv'de SeCreateGlobalPrivilege yok). Yazma hızını APO'nun okuma başlığına
    /// kilitleriz: tampon hep ~Lead kadar dolu kalır → saat kayması birikmez, kesinti olmaz.
    public sealed unsafe class MicBus : IDisposable
    {
        public static string Name = @"Local\zSoundMicBus";

        // native/zsapo.cpp içindeki Bus struct'ı ile aynı yerleşim
        const uint Magic = 0x7A534E44;
        const int Frames = 16384, Mask = Frames - 1;
        const int OffMagic = 0, OffRate = 8, OffCapacity = 16, OffWritePos = 24, OffSoundActive = 32, OffMicGain = 36,
                  OffDuckLevel = 40, OffMicMuted = 44, OffReaders = 48, OffHeartbeat = 56, OffReadHead = 64, OffData = 88;
        const uint Version = 2;   // 72: owner, 80: ownerBeat (APO'lar arasında)
        const long Size = OffData + Frames * 2 * 4;

        public const int Rate = 48000;
        const int MaxChunk = Rate / 10;

        public readonly Mixer Mixer = new Mixer(Rate, 2);
        public volatile float MicGain = 1f, DuckLevel = 1f;
        public volatile bool MicMuted, SendOn = true;
        public volatile int LeadMs = 40;

        IntPtr map;
        byte* view;
        readonly Thread thread;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly float[] buf = new float[MaxChunk * 2];
        volatile bool quit;

        public MicBus()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "zSound mic bus", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        /// şu an mikrofonu işleyen APO sayısı (0 = kimse mikrofonu kullanmıyor)
        public int Readers { get { var v = view; return v == null ? 0 : Volatile.Read(ref *(int*)(v + OffReaders)); } }

        public void Wake() => wake.Set();

        /// belleği oluşturur (ya da APO hâlâ tutuyorsa eskisini alır) ve başlığı hazırlar
        bool TryOpen()
        {
            IntPtr sd = IntPtr.Zero;
            try
            {
                // Erişim: ses motoru (audiodg: LocalService), SYSTEM, yöneticiler ve oturum açmış kullanıcı.
                // Bütünlük etiketi yok (varsayılan orta): tarayıcı sekmesi gibi düşük yetkili süreçler
                // mikrofona ses basamaz.
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        "D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GA;;;OW)(A;;GA;;;IU)", 1, out sd, IntPtr.Zero))
                    return false;
                var sa = new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), Descriptor = sd };
                map = CreateFileMappingW(new IntPtr(-1), ref sa, 0x04 /* PAGE_READWRITE */, 0, (uint)Size, Name);
                if (map == IntPtr.Zero) { Log("bellek oluşturulamadı, hata " + Marshal.GetLastWin32Error()); return false; }
                var p = (byte*)MapViewOfFile(map, 0x0002 | 0x0004 /* WRITE | READ */, 0, 0, (UIntPtr)Size);
                if (p == null) { Close(); return false; }
                if (*(uint*)(p + OffMagic) != Magic || *(uint*)(p + 4) != Version || *(uint*)(p + OffCapacity) != Frames || *(uint*)(p + OffRate) != Rate)
                {
                    new Span<byte>(p, OffData).Clear();
                    *(uint*)(p + 4) = Version;               // version
                    *(uint*)(p + OffRate) = Rate;
                    *(uint*)(p + 12) = 2;                // channels
                    *(uint*)(p + OffCapacity) = Frames;
                    *(float*)(p + OffMicGain) = 1f;
                    *(float*)(p + OffDuckLevel) = 1f;
                    Thread.MemoryBarrier();
                    *(uint*)(p + OffMagic) = Magic;
                }
                view = p;
                Log("bellek hazır: " + Name + " oturum " + Process.GetCurrentProcess().SessionId);
                return true;
            }
            catch { Close(); return false; }
            finally { if (sd != IntPtr.Zero) LocalFree(sd); }
        }

        void Close()
        {
            var v = view;
            view = null;
            if (v != null) UnmapViewOfFile((IntPtr)v);
            if (map != IntPtr.Zero) { CloseHandle(map); map = IntPtr.Zero; }
        }

        void Run()
        {
            int task = 0;
            IntPtr mm = IntPtr.Zero;
            try { mm = AvSetMmThreadCharacteristicsW("Pro Audio", ref task); } catch { }
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2 /* HIGH_RESOLUTION */, 0x1F0003);

            var clock = Stopwatch.StartNew();
            double lastSec = 0, clockFrac = 0;
            long lastHead = -1, headMovedAt = 0, nextOpen = 0;
            int lastReaders = -1;
            bool wasPlaying = false;

            while (!quit)
            {
                long now = Environment.TickCount64;
                if (view == null && now >= nextOpen)
                {
                    if (!TryOpen()) nextOpen = now + 1000;
                }

                byte* v = view;
                long w = 0, head = 0;
                int readers = 0;
                if (v != null)
                {
                    Volatile.Write(ref *(long*)(v + OffHeartbeat), now);
                    *(float*)(v + OffMicGain) = MicGain;
                    *(float*)(v + OffDuckLevel) = DuckLevel;
                    Volatile.Write(ref *(int*)(v + OffMicMuted), MicMuted ? 1 : 0);
                    w = Volatile.Read(ref *(long*)(v + OffWritePos));
                    head = Volatile.Read(ref *(long*)(v + OffReadHead));
                    readers = Volatile.Read(ref *(int*)(v + OffReaders));
                    if (readers != lastReaders) { Log("eklenti bağlantısı: " + readers + " okuyucu"); lastReaders = readers; }
                    if (head != lastHead) { lastHead = head; headMovedAt = now; }
                }

                double sec = clock.Elapsed.TotalSeconds;
                double elapsed = sec - lastSec;
                lastSec = sec;
                bool voices = Mixer.HasVoices;
                int n = 0;
                if (voices)
                {
                    int lead = Math.Clamp(LeadMs, 20, 100) * Rate / 1000;
                    // APO okumuyorsa (kimse mikrofonu açmadı / akış durdu) sesler yine de saatle ilerlesin
                    bool stalled = w - head >= lead && now - headMovedAt > 300;
                    if (readers > 0 && !stalled)
                    {
                        if (w < head) w = head;
                        n = (int)Math.Clamp(head + lead - w, 0, MaxChunk);
                        clockFrac = 0;
                    }
                    else
                    {
                        clockFrac += elapsed * Rate;
                        n = (int)Math.Min(clockFrac, MaxChunk);
                        clockFrac = Math.Min(clockFrac - n, MaxChunk);
                    }
                }
                else clockFrac = 0;

                if (voices != wasPlaying)
                {
                    wasPlaying = voices;
                    Log(voices ? $"ses başladı: okuyucu {readers}, mod {(readers > 0 ? "eklentiye göre" : "saat (eklenti okumuyor!)")}, yaz {w} oku {head}" : $"ses bitti: yaz {w} oku {head}");
                }

                if (n > 0)
                {
                    Mixer.Read(buf, 0, n * 2);
                    if (v != null)
                    {
                        float* ring = (float*)(v + OffData);
                        for (int i = 0; i < n; i++)
                        {
                            int k = (int)((w + i) & Mask) * 2;
                            ring[k] = buf[2 * i];
                            ring[k + 1] = buf[2 * i + 1];
                        }
                        Thread.MemoryBarrier(); // veri, writePos'tan önce görünür olsun
                        Volatile.Write(ref *(long*)(v + OffWritePos), w + n);
                    }
                }

                if (v != null) Volatile.Write(ref *(int*)(v + OffSoundActive), SendOn && Mixer.HasAudible ? 1 : 0);

                if (!voices && now - Mixer.IdleSince > 1000)
                {
                    // boşta: CPU 0. Saniyede bir yaşam sinyali, çalınca Wake() uyandırır
                    wake.WaitOne(1000);
                    lastSec = clock.Elapsed.TotalSeconds;
                }
                else if (timer != IntPtr.Zero)
                {
                    long due = -30_000; // 3 ms
                    SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(timer, 100);
                }
                else Thread.Sleep(2);
            }

            if (timer != IntPtr.Zero) CloseHandle(timer);
            if (mm != IntPtr.Zero) try { AvRevertMmThreadCharacteristics(mm); } catch { }
        }

        /// teşhis: %AppData%\zSound\mic.log (sadece olay olunca yazar)
        static void Log(string msg)
        {
            try
            {
                var path = System.IO.Path.Combine(zSound.Core.Config.Dir, "mic.log");
                System.IO.Directory.CreateDirectory(zSound.Core.Config.Dir);
                if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 256 * 1024) System.IO.File.Delete(path);
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        public void Dispose()
        {
            quit = true;
            wake.Set();
            thread.Join(500);
            var v = view;
            if (v != null)
            {
                // APO mikrofona hemen dokunmayı bıraksın (3 sn beklemeden)
                Volatile.Write(ref *(int*)(v + OffSoundActive), 0);
                Volatile.Write(ref *(long*)(v + OffHeartbeat), 0);
            }
            Close();
            Mixer.Clear();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int Length; public IntPtr Descriptor; public int Inherit; }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int rev, out IntPtr sd, IntPtr size);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileMappingW(IntPtr file, ref SECURITY_ATTRIBUTES sa, uint protect, uint sizeHigh, uint sizeLow, string name);
        [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr size);
        [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr fn, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref int index);
        [DllImport("avrt.dll")] static extern bool AvRevertMmThreadCharacteristics(IntPtr h);
    }
}
