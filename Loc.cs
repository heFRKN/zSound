using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace zSound
{
    /// Arayüz dili. XAML: {DynamicResource L.anahtar} — dil değişince anında güncellenir.
    /// Kod: L.T("anahtar"), L.F("anahtar", argümanlar). Yeni dil = tabloya bir sütun.
    public static class L
    {
        public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("tr", "Türkçe") };

        public static string Current { get; private set; } = "en";
        public static event Action Changed;

        /// boş/bilinmeyen kod → Windows dili Türkçe ise tr, değilse en
        public static string Resolve(string code) =>
            code == "en" || code == "tr" ? code
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";

        public static void Set(string code)
        {
            Current = Resolve(code);
            var res = Application.Current?.Resources;
            if (res != null) foreach (var k in Table.Keys) res["L." + k] = T(k);
            Changed?.Invoke();
        }

        public static string T(string key) =>
            Table.TryGetValue(key, out var v) ? (Current == "tr" ? v.tr : v.en) : key;

        public static string F(string key, params object[] args) => string.Format(T(key), args);

        static readonly Dictionary<string, (string en, string tr)> Table = new()
        {
            // başlık / kütüphane
            ["statusReady"] = ("Ready", "Hazır"),
            ["tipSettings"] = ("Settings", "Ayarlar"),
            ["search"] = ("Search…  (Ctrl+F)", "Ara…  (Ctrl+F)"),
            ["tipFavsOnly"] = ("Favorites only", "Sadece favoriler"),
            ["tipAdd"] = ("Add sound", "Ses ekle"),
            ["emptyTitle"] = ("Drag audio or video files here", "Ses veya video dosyalarını buraya sürükle"),
            ["drop"] = ("Drop to add", "Bırak, ekleyeyim"),
            ["copying"] = ("Copying…", "Kopyalanıyor…"),
            ["copyingN"] = ("Copying… ({0}/{1})", "Kopyalanıyor… ({0}/{1})"),
            ["addFailed"] = ("Couldn't add: {0} ({1})", "Eklenemedi: {0} ({1})"),
            ["menuAddFiles"] = ("Add files…", "Dosya ekle…"),
            ["menuAddFolder"] = ("Add folder…", "Klasör ekle…"),
            ["dlgPick"] = ("Choose audio / video", "Ses / video seç"),
            ["dlgFilter"] = ("Audio and video", "Ses ve video"),
            ["dlgAll"] = ("All files", "Tüm dosyalar"),
            ["dlgFolder"] = ("Choose folder (subfolders included)", "Klasör seç (alt klasörler dahil)"),
            ["play"] = ("Play", "Çal"),
            ["stop"] = ("Stop", "Durdur"),
            ["rowEdit"] = ("Trim / edit", "Kırp / düzenle"),
            ["rowAssignKey"] = ("Assign shortcut", "Kısayol ata"),
            ["rowUp"] = ("Move up", "Yukarı taşı"),
            ["rowDown"] = ("Move down", "Aşağı taşı"),
            ["rowFav"] = ("Add to favorites", "Favorilere ekle"),
            ["rowUnfav"] = ("Remove from favorites", "Favoriden çıkar"),
            ["rowOpenLoc"] = ("Open file location", "Dosya konumunu aç"),
            ["remove"] = ("Remove", "Kaldır"),
            ["removeN"] = ("Remove {0} sounds", "{0} sesi kaldır"),
            ["notFound"] = ("File not found: {0}", "Dosya bulunamadı: {0}"),
            ["subMicOnly"] = (" · mic only", " · sadece mikrofon"),
            ["subMeOnly"] = (" · only me", " · sadece ben"),
            ["subLoop"] = (" · loop", " · döngü"),
            ["subHold"] = (" · hold", " · basılı tut"),

            // alt bar / tepsi
            ["tipPause"] = ("Pause / resume", "Duraklat / devam"),
            ["tipStopAll"] = ("Stop all", "Hepsini durdur"),
            ["tipSendMic"] = ("Send sounds to the mic (on/off)", "Sesleri mikrofona gönder (aç/kapa)"),
            ["them"] = ("THEM", "ONLAR"),
            ["tipThemVol"] = ("Level people on Discord hear", "Discord'dakilerin duyduğu seviye"),
            ["tipHearToo"] = ("Let me hear too (on/off)", "Ben de duyayım (aç/kapa)"),
            ["me"] = ("ME", "BEN"),
            ["tipMeVol"] = ("Level you hear in your headphones (independent of theirs)", "Senin kulaklığında duyduğun seviye (onlarınkinden bağımsız)"),
            ["trayShow"] = ("Show", "Göster"),
            ["trayExit"] = ("Exit", "Çıkış"),

            // durum
            ["stSpkOnly"] = ("Speakers only", "Sadece hoparlör"),
            ["stSpkOnlyTip"] = ("Microphone plugin not installed — Settings → Microphone plugin → Install", "Mikrofon eklentisi kurulu değil — Ayarlar → Mikrofon eklentisi → Kur"),
            ["stUpdate"] = ("Update", "Güncelle"),
            ["stUpdateTip"] = ("Microphone plugin is outdated — Settings → Microphone plugin → Update", "Mikrofon eklentisi eski — Ayarlar → Mikrofon eklentisi → Güncelle"),
            ["stError"] = ("Error", "Hata"),
            ["stMuted"] = ("Mic muted", "Sesin kapalı"),
            ["stMutedTip"] = ("Your own voice is muted (use the shortcut to unmute)", "Kendi sesin susturuldu (kısayolla tekrar aç)"),
            ["stPlaying"] = ("Playing", "Çalıyor"),
            ["stActive"] = (" · active", " · etkin"),
            ["stIdle"] = (" · no app is using the mic right now", " · şu an mikrofonu kullanan uygulama yok"),

            // ayarlar
            ["secDevices"] = ("DEVICES", "CİHAZLAR"),
            ["devMonitor"] = ("What you hear (speakers / headphones)", "Senin duyduğun (hoparlör / kulaklık)"),
            ["devMic"] = ("Your real microphone", "Gerçek mikrofonun"),
            ["devMicHint"] = ("Sounds come straight out of this microphone; keep it selected in Discord and games.", "Sesler doğrudan bu mikrofondan çıkar; Discord'da ve oyunlarda bu mikrofon seçili kalsın."),
            ["devRefresh"] = ("Refresh devices", "Cihazları yenile"),
            ["devDefMon"] = ("Default speakers", "Varsayılan hoparlör"),
            ["devDefMic"] = ("Default microphone", "Varsayılan mikrofon"),
            ["secMic"] = ("MICROPHONE", "MİKROFON"),
            ["apoTitle"] = ("Microphone plugin", "Mikrofon eklentisi"),
            ["apoRemoveTip"] = ("Removes the plugin from all microphones and restores everything", "Eklentiyi tüm mikrofonlardan kaldırır, her şeyi eski haline döndürür"),
            ["apoInstall"] = ("Install", "Kur"),
            ["apoUpdate"] = ("Update", "Güncelle"),
            ["apoReinstall"] = ("Reinstall", "Yeniden kur"),
            ["apoNoMic"] = ("Microphone not found.", "Mikrofon bulunamadı."),
            ["apoElsewhere"] = ("Installed on another microphone. ", "Başka bir mikrofonda kurulu. "),
            ["apoWillRoute"] = ("Once installed, sounds go straight through \"{0}\". Requires admin permission.", "Kurulunca sesler doğrudan \"{0}\" üzerinden gider. Yönetici izni ister."),
            ["apoOld"] = ("The installed plugin is outdated; sounds won't reach the mic. Press \"Update\".", "Kurulu eklenti eski sürüm, sesler mikrofona gitmez. \"Güncelle\"ye bas."),
            ["apoActive"] = ("Active — sounds are going through \"{0}\".", "Etkin — sesler \"{0}\" üzerinden gidiyor."),
            ["apoIdle"] = ("Installed. Activates when an app (Discord, a game, Windows sound settings…) opens the microphone.", "Kurulu. Mikrofonu bir uygulama (Discord, oyun, Windows ses ayarları…) açınca etkinleşir."),
            ["apoConfirmInstall"] = (
                "The zSound plugin will be installed on the \"{0}\" microphone.\n\n" +
                "Admin permission will be requested and the Windows audio service will restart: all audio cuts out for a few seconds, " +
                "and Discord reconnects the microphone on its own.\n\n" +
                "After installing, a 3-second test tone is sent to the microphone and measured; if it doesn't get through, everything is rolled back automatically.\n\nContinue?",
                "zSound eklentisi \"{0}\" mikrofonuna kurulacak.\n\n" +
                "Yönetici izni istenecek ve Windows ses hizmeti yeniden başlatılacak: tüm sesler birkaç saniye kesilir, " +
                "Discord mikrofonu kendiliğinden yeniden bağlanır.\n\n" +
                "Kurulumdan sonra mikrofona 3 saniyelik bir test sesi gönderilip ölçülür; ses geçmezse her şey otomatik geri alınır.\n\nDevam edilsin mi?"),
            ["apoConfirmRemove"] = (
                "The plugin will be removed from all microphones and everything it changed will be restored.\n\n" +
                "Admin permission will be requested and the Windows audio service will restart (audio cuts out for a few seconds).\n\nContinue?",
                "Eklenti tüm mikrofonlardan kaldırılacak ve değiştirilen her şey eski haline dönecek.\n\n" +
                "Yönetici izni istenecek ve Windows ses hizmeti yeniden başlatılacak (sesler birkaç saniye kesilir).\n\nDevam edilsin mi?"),
            ["apoInstalling"] = ("Installing…", "Kuruluyor…"),
            ["apoRemoving"] = ("Removing…", "Kaldırılıyor…"),
            ["apoRestarting"] = (" (audio service restarting)", " (ses hizmeti yeniden başlıyor)"),
            ["micGain"] = ("Microphone gain", "Mikrofon kazancı"),
            ["duckTitle"] = ("Lower the mic while a sound plays", "Ses çalarken mikrofonu kıs"),
            ["duckHint"] = ("Ducking — so the sound comes through clearly", "Ducking — ses net duyulsun"),
            ["duckLevel"] = ("Ducking level", "Kısma seviyesi"),
            ["pttTitle"] = ("Push-to-talk key", "Bas-konuş tuşu"),
            ["pttHint"] = ("Holds this key down automatically while a sound plays", "Ses çalarken bu tuşu otomatik basılı tutar"),
            ["off"] = ("Off", "Kapalı"),
            ["secPlay"] = ("PLAYBACK", "ÇALMA"),
            ["ovTitle"] = ("When pressed while playing", "Aynı anda basınca"),
            ["ovOverlap"] = ("Play on top", "Üst üste çal"),
            ["ovRestart"] = ("Restart the same sound", "Aynı sesi baştan al"),
            ["ovStopOthers"] = ("Stop the others", "Diğerlerini durdur"),
            ["againTitle"] = ("Press again to stop", "Tekrar basınca durdur"),
            ["againHint"] = ("A playing sound stops when its key is pressed a second time", "Çalan sesin tuşu ikinci kez basılınca susar"),
            ["normTitle"] = ("Even out loudness", "Ses yüksekliğini eşitle"),
            ["normHint"] = ("All sounds play at the same loudness", "Tüm sesler aynı yükseklikte çalar"),
            ["boostTitle"] = ("Microphone boost", "Mikrofon güçlendirme"),
            ["boostHint"] = ("Others hear it louder", "Karşı taraf daha yüksek duyar"),
            ["limHint"] = ("Prevents clipping / crackling", "Patlamayı/cızırtıyı engeller"),
            ["latTitle"] = ("Latency", "Gecikme"),
            ["latHint"] = ("Lower = faster response; raise it if you hear crackling", "Düşük = hızlı tepki, cızırtı olursa yükselt"),
            ["secGlobalKeys"] = ("GLOBAL SHORTCUTS", "GENEL KISAYOLLAR"),
            ["keysHint"] = ("Click → press a key, release. For a combo, hold one and press the other (Num1+Num2). Right-click clears. Mouse side buttons work too.",
                            "Tıkla → tuşa bas, bırak. Kombinasyon için birini basılı tutup diğerine bas (Num1+Num2). Sağ tık siler. Fare yan tuşları da olur."),
            ["gkStopAll"] = ("Stop all", "Hepsini durdur"),
            ["gkPause"] = ("Pause / resume", "Duraklat / devam"),
            ["gkRandom"] = ("Random sound", "Rastgele ses"),
            ["gkReplay"] = ("Replay last sound", "Son sesi tekrar çal"),
            ["gkMic"] = ("Toggle send to mic", "Mikrofona gönder aç/kapa"),
            ["gkSpk"] = ("Toggle speakers", "Hoparlör aç/kapa"),
            ["gkMute"] = ("Mute / unmute my voice", "Kendi sesimi sustur / aç"),
            ["keyAssign"] = ("Set", "Ata"),
            ["keyPress"] = ("Press a key…", "Bir tuşa bas…"),
            ["secGeneral"] = ("GENERAL", "GENEL"),
            ["genTray"] = ("Minimize to tray on close", "Kapatınca tepsiye küçült"),
            ["genStartup"] = ("Start with Windows", "Windows ile başlat"),
            ["genStartMin"] = ("Start minimized", "Küçültülmüş başlat"),
            ["genGpu"] = ("Draw with GPU", "GPU ile çiz"),
            ["genGpuHint"] = ("Off: 0 VRAM. On: less CPU while browsing the UI. Applies after restart.", "Kapalı: VRAM 0. Açık: arayüz gezinirken daha az CPU. Yeniden başlatınca geçerli."),
            ["genOpenCfg"] = ("Open settings folder", "Ayar klasörünü aç"),

            // düzenleyici
            ["editBackTip"] = ("Back (Esc)", "Geri (Esc)"),
            ["editTryTip"] = ("Plays where it's set to (including the mic)", "Ayarlandığı yere çalar (mikrofon dahil)"),
            ["editTry"] = ("Try", "Dene"),
            ["editTitle"] = ("Trim & edit", "Kırp ve düzenle"),
            ["editLoading"] = ("Loading waveform…", "Dalga formu yükleniyor…"),
            ["editReadFail"] = ("Couldn't read: {0}", "Okunamadı: {0}"),
            ["playFail"] = ("Couldn't play", "Çalınamadı"),
            ["editPlayTip"] = ("Listen to the selection (Space)", "Seçili kısmı dinle (Boşluk)"),
            ["editHead"] = ("Start", "Başı"),
            ["editHeadTip"] = ("Listen to the cut start (first 1.5 s)", "Kesilen başlangıcı dinle (ilk 1.5 sn)"),
            ["editTail"] = ("End", "Sonu"),
            ["editTailTip"] = ("Listen to the cut end (last 1.5 s)", "Kesilen sonu dinle (son 1.5 sn)"),
            ["zoomOut"] = ("Zoom out", "Uzaklaş"),
            ["zoomIn"] = ("Zoom in (wheel)", "Yakınlaş (tekerlek)"),
            ["zoomSel"] = ("Zoom to selection", "Seçime yakınlaş"),
            ["zoomFit"] = ("Show all", "Tamamını göster"),
            ["nudgeStartMinus"] = ("Start −0.1 s (Shift: 0.01 s)", "Başlangıç −0.1 sn (Shift: 0.01 sn)"),
            ["nudgeStartPlus"] = ("Start +0.1 s (Shift: 0.01 s)", "Başlangıç +0.1 sn (Shift: 0.01 sn)"),
            ["nudgeEndMinus"] = ("End −0.1 s (Shift: 0.01 s)", "Bitiş −0.1 sn (Shift: 0.01 sn)"),
            ["nudgeEndPlus"] = ("End +0.1 s (Shift: 0.01 s)", "Bitiş +0.1 sn (Shift: 0.01 sn)"),
            ["trimStart"] = ("START", "BAŞLANGIÇ"),
            ["trimLen"] = ("LENGTH", "UZUNLUK"),
            ["trimEnd"] = ("END", "BİTİŞ"),
            ["audTitle"] = ("Listen on release", "Bırakınca dinle"),
            ["audTip"] = ("Briefly plays the cut point when you release an edge", "Kenarı bırakınca kestiğin yeri kısaca çalar"),
            ["trimAll"] = ("Use whole", "Tamamını al"),
            ["editHint"] = ("Drag the edges · wheel: zoom · Shift+wheel or right-drag: scroll · click the ruler on top: listen from there · Space: play/stop",
                            "Kenarları sürükle · tekerlek: yakınlaş · Shift+tekerlek ya da sağ tık sürükle: kaydır · üstteki cetvele tıkla: oradan dinle · Boşluk: çal/durdur"),
            ["editName"] = ("Name", "İsim"),
            ["editKey"] = ("Shortcut", "Kısayol"),
            ["editTarget"] = ("Play to", "Nereye çalsın"),
            ["tgtBoth"] = ("Mic + speakers", "Mikrofon + hoparlör"),
            ["tgtMic"] = ("Mic only", "Sadece mikrofon"),
            ["tgtMe"] = ("Only I hear it", "Sadece ben duyayım"),
            ["secSound"] = ("SOUND", "SES"),
            ["volume"] = ("Volume", "Ses seviyesi"),
            ["speed"] = ("Speed / pitch", "Hız / perde"),
            ["speedHint"] = ("Chipmunk ↔ deep voice", "Sincap ↔ derin ses"),
            ["fadeIn"] = ("Fade in", "Yavaşça gir (fade in)"),
            ["fadeOut"] = ("Fade out", "Yavaşça çık (fade out)"),
            ["secBehavior"] = ("BEHAVIOR", "DAVRANIŞ"),
            ["loop"] = ("Loop", "Döngü"),
            ["hold"] = ("Play while held", "Basılı tuttukça çal"),
            ["holdHint"] = ("Stops when the key is released", "Tuşu bırakınca susar"),
            ["fav"] = ("Favorite", "Favori"),
            ["editReset"] = ("Reset to defaults", "Varsayılana döndür"),

            // ses motoru / mikrofon eklentisi
            ["engSpeaker"] = ("Speakers: {0}", "Hoparlör: {0}"),
            ["engNoOutput"] = ("No output device", "Çıkış cihazı yok"),
            ["engOpenFail"] = ("Couldn't open: {0} ({1})", "Açılamadı: {0} ({1})"),
            ["engNoSpeaker"] = ("Speakers not found", "Hoparlör bulunamadı"),
            ["apoFailCode"] = ("Operation failed (code {0})", "İşlem başarısız (kod {0})"),
            ["apoDenied"] = ("Admin permission was not granted", "Yönetici izni verilmedi"),
            ["apoVerifyFail"] = ("Installation couldn't be verified; everything was rolled back.\n\n{0}", "Kurulum doğrulanamadı, her şey geri alındı.\n\n{0}"),
            ["apoUnknownCmd"] = ("Unknown command: {0}", "Bilinmeyen komut: {0}"),
            ["apoMicMissing"] = ("Microphone not found", "Mikrofon bulunamadı"),
            ["apoMicNotInReg"] = ("Microphone is not in the registry", "Mikrofon kayıt defterinde yok"),
            ["apoNoDll"] = ("zsapo.dll is missing from the exe", "zsapo.dll exe içinde yok"),
            ["apoRegOpen"] = ("Couldn't open registry key: {0}", "Kayıt defteri açılamadı: {0}"),
            ["apoSlotsFull"] = ("All effect slots on this microphone are taken by the driver's own effects; nothing was overwritten.", "Bu mikrofonun bütün efekt yuvaları sürücünün kendi efektleriyle dolu; üzerine yazılmadı."),
            ["probeNoData"] = ("No data received from the microphone.", "Mikrofondan veri alınamadı."),
            ["probeFail"] = ("Microphone passthrough test failed: {0}", "Mikrofon ses aktarım testi başarısız: {0}"),
            ["probeStopped"] = ("Microphone stream stopped after the test.", "Test sonrası mikrofon akışı durdu."),
        };
    }
}
