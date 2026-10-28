using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace zSound.Core
{
    /// Mikrofon eklentisinin (native/zsapo.dll) kurulumu. APO'yu gerçek mikrofonun "ses iyileştirmeleri"
    /// zincirine (EFX yuvası) ekler; Discord/oyunlar aynı mikrofonu seçili tutar. Yazma işleri yönetici
    /// izniyle ayrı bir zSound sürecinde (--apo) yapılır. Kurulumdan sonra mikrofondan ses geçtiği ölçülür,
    /// geçmezse her şey geri alınır. Değiştirilen her değer yedeklenir, kaldırınca birebir geri konur.
    public static class MicApo
    {
        public const string Clsid = "{5A7C3E91-2B4D-4F6A-9E1C-7D3B8A2F4C60}";   // native/zsapo.cpp CLSID_ZSoundApo
        const string FxGuid = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
        const string EfxKey = FxGuid + ",7", CompositeEfxKey = FxGuid + ",15";
        const string ModesGuid = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}";   // PKEY_*_ProcessingModes_Supported_For_Streaming
        /// (tek değer, bileşik liste, desteklenen modlar) — SFX, MFX, EFX
        static readonly (string plain, string composite, string modes)[] Slots =
        {
            (FxGuid + ",5", FxGuid + ",13", ModesGuid + ",5"),
            (FxGuid + ",6", FxGuid + ",14", ModesGuid + ",6"),
            (FxGuid + ",7", FxGuid + ",15", ModesGuid + ",7"),
        };
        /// varsayılan, iletişim (Discord), konuşma, ham
        static readonly string[] Modes =
        {
            "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}", "{98951333-B9CD-48B1-A0A3-FF40682D73F7}",
            "{FC1CFC9B-B9D6-4CFA-B5E0-4BB2166878B2}", "{9E90EA20-B493-4FD1-A1A8-7E1361A956CF}",
        };
        const string SysFxOffKey = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";  // "ses iyileştirmelerini kapat"
        const string CaptureRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\";
        const string ClsidKey = @"SOFTWARE\Classes\CLSID\" + Clsid;
        const string ApoRegKey = @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + Clsid;
        const string AudioKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";
        const string RootKey = @"SOFTWARE\zSound", BackupKey = RootKey + @"\MicApo";

        static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "zSound");
        static string DllPath => Path.Combine(Dir, "zsapo.dll");
        /// eski sürümlerin teşhis klasörü (kaldırırken temizlenir)
        static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "zSound");
        static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        /// "{0.0.1.00000000}.{guid}" → "{guid}"
        static string EndpointGuid(string deviceId)
        {
            int i = deviceId?.LastIndexOf('{') ?? -1;
            if (i < 0) return null;
            var g = deviceId[i..];
            return Guid.TryParse(g, out _) ? g : null;
        }

        static bool IsOurs(string v) => string.Equals(v, Clsid, StringComparison.OrdinalIgnoreCase);
        /// uç noktayı bir Windows efekt paketine bağlayan değer ("{paket mod CLSID},100")
        static bool IsPackLink(string name) => name.EndsWith(",100", StringComparison.Ordinal);
        static bool HasOurs(object v) => v is string s ? IsOurs(s) : v is string[] a && a.Any(IsOurs);

        // ───────── durum (yönetici gerekmez) ─────────

        /// bu mikrofonun zincirinde zSound eklentisi var mı
        public static bool IsInstalledOn(string deviceId)
        {
            var g = EndpointGuid(deviceId);
            if (g == null) return false;
            try
            {
                using var hk = Hklm();
                using var fx = hk.OpenSubKey(CaptureRoot + g + @"\FxProperties");
                if (fx == null || !fx.GetValueNames().Any(n => HasOurs(fx.GetValue(n)))) return false;
                // Windows efekt paketini geri bağladıysa eklenti yüklenmez → yeniden kurulmalı
                if (fx.GetValueNames().Any(IsPackLink)) return false;
                using var c = hk.OpenSubKey(ClsidKey + @"\InprocServer32");
                return c != null && File.Exists(DllPath);
            }
            catch { return false; }
        }

        /// Program Files'taki eklenti bu zSound'un içindekinden farklı mı (güncelleme gerekli)
        public static bool NeedsUpdate
        {
            get
            {
                try
                {
                    using var s = typeof(MicApo).Assembly.GetManifestResourceStream("zsapo.dll");
                    var data = new byte[s.Length];
                    s.ReadExactly(data);
                    return !File.ReadAllBytes(DllPath).AsSpan().SequenceEqual(data);
                }
                catch { return false; }
            }
        }

        /// herhangi bir mikrofona kurulu mu (kaldır butonu için)
        public static bool AnyInstalled
        {
            get
            {
                try
                {
                    using var hk = Hklm();
                    using var k = hk.OpenSubKey(ClsidKey);
                    using var b = hk.OpenSubKey(BackupKey);
                    return k != null || b != null;
                }
                catch { return false; }
            }
        }

        // ───────── yönetici süreci başlatma ─────────

        /// kendini yönetici olarak --apo ile çalıştırır. null = başarılı, değilse hata metni
        public static async Task<string> RunElevated(string verb, string deviceId)
        {
            var result = Path.Combine(Path.GetTempPath(), "zsound-apo-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = $"--apo {verb} \"{deviceId ?? "-"}\" \"{result}\""
                };
                using var p = Process.Start(psi);
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) return null;
                var msg = File.Exists(result) ? File.ReadAllText(result).Trim() : "";
                return msg.Length > 0 ? msg : L.F("apoFailCode", p.ExitCode);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return L.T("apoDenied"); }
            catch (Exception e) { return e.Message; }
            finally { try { File.Delete(result); } catch { } }
        }

        /// zSound.exe --apo install|uninstall "deviceId" "sonuç dosyası"  (yönetici olarak, arayüzsüz)
        public static int RunCommand(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "", id = args.Length > 2 ? args[2] : "", result = args.Length > 3 ? args[3] : null;
            try
            {
                EnablePrivilege("SeBackupPrivilege");
                EnablePrivilege("SeRestorePrivilege");
                if (verb == "install")
                {
                    // kur → mikrofondan gerçekten ses geçiyor mu ölç → geçmiyorsa her şeyi geri al
                    // (bozuk bir kurulum mikrofonu tamamen susturabilir)
                    try
                    {
                        Install(id);
                        Audio.MicProbe.Run(id);
                    }
                    catch (Exception e)
                    {
                        try { Uninstall(); } catch { }
                        throw new Exception(L.F("apoVerifyFail", e.Message), e);
                    }
                }
                else if (verb == "uninstall") Uninstall();
                else if (verb == "verify")
                {
                    WriteResult(result, Audio.MicProbe.Run(id));
                }
                else throw new ArgumentException(L.F("apoUnknownCmd", verb));
                return 0;
            }
            catch (Exception e)
            {
                WriteResult(result, e.Message);
                return 1;
            }
        }

        /// Yönetici süreci sonucu çağıranın geçici klasörüne yazar. Sadece yeni dosya oluşturur:
        /// önceden konmuş bir dosya/symlink varsa yazmaz (yönetici yetkisiyle başka dosyaya yazdırılamasın).
        static void WriteResult(string path, string text)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var w = new StreamWriter(f);
                w.Write(text);
            }
            catch { }
        }

        // ───────── kurulum ─────────

        static void Install(string deviceId)
        {
            var g = EndpointGuid(deviceId) ?? throw new Exception(L.T("apoMicMissing"));
            using var hk = Hklm();
            using (var ep = hk.OpenSubKey(CaptureRoot + g))
                if (ep == null) throw new Exception(L.T("apoMicNotInReg"));

            CopyDll();
            Register(hk);

            using (var bk = hk.CreateSubKey(BackupKey + @"\" + g))
            using (var fx = OpenProtected(CaptureRoot + g + @"\FxProperties"))
            {
                // Hangi yuvayı ses motorunun kuracağı cihaza ve moda göre değişiyor: üçüne de yaz
                // (SFX akış, MFX mod, EFX uç nokta). Aynı zincirde birden fazlası yüklenirse
                // eklentide yalnızca biri sesleri karıştırır. Doluysa (sürücünün efekti) dokunma.
                int written = 0;
                foreach (var (plain, composite, modes) in Slots.Skip(2))
                {
                    if (fx.GetValue(composite) is string[] comp)
                    {
                        // Windows 11 bileşik zinciri: sona eklen, diğerlerine dokunma
                        if (!HasOurs(comp)) Backup(fx, bk, composite);
                        if (!HasOurs(comp)) fx.SetValue(composite, comp.Append(Clsid).ToArray(), RegistryValueKind.MultiString);
                        written++;
                    }
                    else
                    {
                        var cur = fx.GetValue(plain) as string;
                        bool free = string.IsNullOrEmpty(cur) || IsOurs(cur) || (Guid.TryParse(cur, out var cg) && cg == Guid.Empty);
                        if (!free) continue;
                        Backup(fx, bk, plain);
                        fx.SetValue(plain, Clsid, RegistryValueKind.String);
                        written++;
                    }
                    // mod listesi yoksa ses motoru bu yuvayı hiç kurmayabilir
                    if (fx.GetValue(modes) == null)
                    {
                        Backup(fx, bk, modes);
                        fx.SetValue(modes, Modes, RegistryValueKind.MultiString);
                    }
                }
                if (written == 0) throw new Exception(L.T("apoSlotsFull"));

                // Windows 11 efekt paketi bağlantısı (Voice Clarity: "{9e6136e0-…},100") varken ses motoru
                // zinciri paketten kurar ve uç noktadaki efektleri (bizimkini de) hiç yüklemez → yedekle, kaldır
                foreach (var n in fx.GetValueNames().Where(IsPackLink))
                {
                    if (bk.GetValue("Pack:" + n) == null) bk.SetValue("Pack:" + n, fx.GetValue(n), fx.GetValueKind(n));
                    fx.DeleteValue(n, false);
                }

                // "ses iyileştirmeleri" kapalıysa APO hiç yüklenmez
                using var props = OpenProtected(CaptureRoot + g + @"\Properties");
                if (props.GetValue(SysFxOffKey) is int off && off != 0)
                {
                    if (bk.GetValue("SysFx") == null) bk.SetValue("SysFx", off, RegistryValueKind.DWord);
                    props.SetValue(SysFxOffKey, 0, RegistryValueKind.DWord);
                }
            }

            RestartAudio();
        }

        static void CopyDll()
        {
            byte[] data;
            using (var s = typeof(MicApo).Assembly.GetManifestResourceStream("zsapo.dll") ?? throw new Exception(L.T("apoNoDll")))
            {
                data = new byte[s.Length];
                s.ReadExactly(data);
            }
            Directory.CreateDirectory(Dir);
            if (File.Exists(DllPath) && File.ReadAllBytes(DllPath).AsSpan().SequenceEqual(data)) return;
            try { File.WriteAllBytes(DllPath, data); }
            catch (IOException)
            {
                // ses motoru eskisini kullanıyor: kenara çek, yenisini yaz; eskisi sonra silinir
                var old = Path.Combine(Dir, "zsapo." + DateTime.Now.Ticks + ".old");
                File.Move(DllPath, old);
                MoveFileExW(old, null, 4 /* DELAY_UNTIL_REBOOT */);
                File.WriteAllBytes(DllPath, data);
            }
        }

        static void Register(RegistryKey hk)
        {
            using (var k = hk.CreateSubKey(ClsidKey))
            {
                k.SetValue("", "zSound Mic");
                using var ip = k.CreateSubKey("InprocServer32");
                ip.SetValue("", DllPath);
                ip.SetValue("ThreadingModel", "Both");
            }
            // RegisterAPO'nun yazdığı kayıt (APO_REG_PROPERTIES ile aynı)
            using (var k = hk.CreateSubKey(ApoRegKey))
            {
                k.SetValue("FriendlyName", "zSound Mic");
                k.SetValue("Copyright", "zSound");
                k.SetValue("MajorVersion", 1, RegistryValueKind.DWord);
                k.SetValue("MinorVersion", 0, RegistryValueKind.DWord);
                k.SetValue("Flags", 0xF, RegistryValueKind.DWord); // INPLACE | DEFAULT
                k.SetValue("MinInputConnections", 1, RegistryValueKind.DWord);
                k.SetValue("MaxInputConnections", 1, RegistryValueKind.DWord);
                k.SetValue("MinOutputConnections", 1, RegistryValueKind.DWord);
                k.SetValue("MaxOutputConnections", 1, RegistryValueKind.DWord);
                k.SetValue("MaxInstances", -1, RegistryValueKind.DWord);
                k.SetValue("NumAPOInterfaces", 1, RegistryValueKind.DWord);
                k.SetValue("APOInterface0", "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}");
            }
            // korumalı audiodg imzasız eklentiyi reddetmesin; eski değer yedekte
            try
            {
                using var a = hk.CreateSubKey(AudioKey);
                using var b = hk.CreateSubKey(BackupKey);
                if (b.GetValue("ProtectedDG") == null)
                    b.SetValue("ProtectedDG", a.GetValue("DisableProtectedAudioDG") is int v ? v.ToString() : "-");
                a.SetValue("DisableProtectedAudioDG", 1, RegistryValueKind.DWord);
            }
            catch { }
        }

        // ───────── kaldırma ─────────

        static void Uninstall()
        {
            using var hk = Hklm();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var b = hk.OpenSubKey(BackupKey))
            {
                foreach (var g in b?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var e = b.OpenSubKey(g);
                    Restore(hk, g, e);
                    done.Add(g);
                }
            }
            // yedek kaybolmuş olsa bile hiçbir mikrofonda iz kalmasın
            using (var cap = hk.OpenSubKey(CaptureRoot.TrimEnd('\\')))
                foreach (var g in cap?.GetSubKeyNames() ?? Array.Empty<string>())
                    if (!done.Contains(g)) Restore(hk, g, null);

            try { hk.DeleteSubKeyTree(ApoRegKey, false); } catch { }
            try { hk.DeleteSubKeyTree(ClsidKey, false); } catch { }
            try
            {
                using var b = hk.OpenSubKey(BackupKey);
                if (b?.GetValue("ProtectedDG") is string prev)
                {
                    using var a = hk.OpenSubKey(AudioKey, true);
                    if (prev == "-") a?.DeleteValue("DisableProtectedAudioDG", false);
                    else a?.SetValue("DisableProtectedAudioDG", int.Parse(prev), RegistryValueKind.DWord);
                }
            }
            catch { }
            try { hk.DeleteSubKeyTree(RootKey, false); } catch { }

            RestartAudio(); // ses motoru DLL'i bıraksın
            DeleteFiles();
        }

        static void Restore(RegistryKey hk, string g, RegistryKey bk)
        {
            bool touch;
            using (var fxr = hk.OpenSubKey(CaptureRoot + g + @"\FxProperties"))
                touch = fxr != null && (bk != null || fxr.GetValueNames().Any(n => HasOurs(fxr.GetValue(n))));
            if (touch)
            {
                using var fx = OpenProtected(CaptureRoot + g + @"\FxProperties");
                // yedekte olanlar: birebir eski hali
                foreach (var n in bk?.GetValueNames() ?? Array.Empty<string>())
                {
                    if (n.StartsWith("Absent:")) fx.DeleteValue(n[7..], false);
                    else if (n.StartsWith("Orig:")) fx.SetValue(n[5..], bk.GetValue(n), bk.GetValueKind(n));
                    else if (n.StartsWith("Pack:")) fx.SetValue(n[5..], bk.GetValue(n), bk.GetValueKind(n));
                }
                // yedekte olmasa da bizim izimiz kalmasın
                foreach (var n in fx.GetValueNames())
                {
                    var v = fx.GetValue(n);
                    if (v is string sv && IsOurs(sv)) fx.DeleteValue(n, false);
                    else if (v is string[] arr && HasOurs(arr)) fx.SetValue(n, arr.Where(x => !IsOurs(x)).ToArray(), RegistryValueKind.MultiString);
                }
                if (bk?.GetValue("Prev") is string prev) fx.SetValue(EfxKey, prev, RegistryValueKind.String); // eski sürüm yedeği
            }
            if (bk?.GetValue("SysFx") is int off)
            {
                using var p = OpenProtected(CaptureRoot + g + @"\Properties");
                p.SetValue(SysFxOffKey, off, RegistryValueKind.DWord);
            }
        }

        /// değeri değiştirmeden önce ilk halini sakla (bir kez); bizim değerimiz ilk hal sayılmaz
        static void Backup(RegistryKey fx, RegistryKey bk, string name)
        {
            if (bk.GetValue("Orig:" + name) != null || bk.GetValue("Absent:" + name) != null) return;
            var v = fx.GetValue(name);
            if (v is string sv && IsOurs(sv)) v = null;
            if (v is string[] arr && HasOurs(arr)) v = arr.Where(x => !IsOurs(x)).ToArray();
            if (v == null) bk.SetValue("Absent:" + name, 1, RegistryValueKind.DWord);
            else bk.SetValue("Orig:" + name, v, fx.GetValueKind(name));
        }

        static void DeleteFiles()
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir, "zsapo*"))
            {
                try { File.Delete(f); }
                catch { MoveFileExW(f, null, 4 /* DELAY_UNTIL_REBOOT */); }
            }
            try { Directory.Delete(Dir); } catch { }
            try { Directory.Delete(DataDir, true); } catch { }
        }

        // ───────── yardımcılar ─────────

        /// ses motoru FxProperties'i ancak yeniden başlayınca okur (birkaç saniye ses kesilir)
        static void RestartAudio()
        {
            Net("stop Audiosrv /y");
            Net("stop AudioEndpointBuilder /y");
            Net("start AudioEndpointBuilder");
            Net("start Audiosrv");
        }

        static void Net(string args) => Run("net.exe", args);

        static void Run(string exe, string args)
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            p.WaitForExit(30000);
        }

        /// uç nokta anahtarları TrustedInstaller'ın: ACL'ye dokunmadan yedekleme/geri yükleme yetkisiyle açar
        static RegistryKey OpenProtected(string path)
        {
            int r = RegCreateKeyExW(new IntPtr(unchecked((int)0x80000002)), path, 0, null, 4 /* REG_OPTION_BACKUP_RESTORE */,
                                    0, IntPtr.Zero, out var h, out _);
            if (r != 0) throw new Win32Exception(r, L.F("apoRegOpen", path));
            return RegistryKey.FromHandle(h, RegistryView.Registry64);
        }

        static void EnablePrivilege(string name)
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28 /* ADJUST | QUERY */, out var tok)) return;
            try
            {
                var tp = new TOKEN_PRIVILEGES { Count = 1, Attr = 2 /* ENABLED */ };
                if (LookupPrivilegeValueW(null, name, out tp.Luid))
                    AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(tok); }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attr; }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegCreateKeyExW(IntPtr key, string sub, int reserved, string cls, int options, int sam, IntPtr sa, out SafeRegistryHandle result, out int disp);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, int access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValueW(string sys, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool MoveFileExW(string from, string to, int flags);
    }
}
