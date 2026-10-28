# zSound

A lightweight soundboard for Windows that plays sounds **directly through your real microphone** — no virtual cable, no changing your default mic. Discord and games keep using your own microphone; your voice and your sounds go out together.

[English](#english) · [Türkçe](#türkçe)

---

## English

### Features
- Sounds go out through your **real microphone** (via a small Windows audio plugin, the same technique Soundpad uses)
- Your own voice keeps working; optional **ducking** lowers your mic while a sound plays
- Play many sounds at once, with a limiter so nothing clips
- Global hotkeys (including key combos like `Num1+Num2` and mouse side buttons), hold-to-play, loop
- Trim editor with waveform, fade in/out, speed/pitch, per-sound volume, loudness normalization
- Separate volume for "them" (mic) and "me" (your headphones)
- Optional automatic push-to-talk key while a sound is playing
- Drag & drop audio **and video** files (mp3, wav, ogg, flac, m4a, mp4, mkv, webm…)
- Tray mode, starts with Windows, very low CPU/RAM when idle

> The user interface is currently in Turkish.

### Requirements
- Windows 10 or 11, 64-bit
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)
- Administrator permission once, to install the microphone plugin

### Install & first use
1. Download `zSound.exe` from [Releases](../../releases) and run it.
2. Open **Settings** (gear icon) → under *CİHAZLAR* pick:
   - **Senin duyduğun** = your headphones/speakers
   - **Gerçek mikrofonun** = your real microphone
3. Under *MİKROFON* → **Mikrofon eklentisi** → click **Kur** (Install) and accept the admin prompt.
   - Windows audio restarts for a few seconds.
   - A **3-second test tone** is sent through your mic and measured. If it doesn't come through, everything is rolled back automatically.
4. When the status says **"Etkin"** (active), sounds you play go out through your microphone.

### Usage
- Drag files onto the window (or use **+**).
- Double-click / Enter to play. Right-click a sound to edit, trim or assign a hotkey.
- Bottom bar: **ONLAR** = volume others hear, **BEN** = volume you hear.
- Command line (while zSound is running): `zSound.exe --play "name"`, `--stop`, `--pause`, `--random`, `--add "file"`.

### Discord tips (important)
Discord processes sounds like your voice and can cut them. In *User Settings → Voice & Video*:
- **Echo Cancellation: OFF** (most important — otherwise Discord removes the sound because it also plays in your headphones)
- **Noise Suppression: None**
- **Automatic Gain Control: OFF**
- Use **Push to Talk**, or turn off automatic input sensitivity and lower the threshold.

### Uninstall
Settings → **Mikrofon eklentisi → Kaldır** (Remove). Every registry value the installer changed is restored exactly as it was, and the plugin files are deleted. Then delete `zSound.exe` and `%AppData%\zSound` (your settings).

### What the installer changes (transparency)
- Copies `zsapo.dll` to `C:\Program Files\zSound\`
- Registers it as a COM class and as an audio processing object (`HKLM\SOFTWARE\Classes`)
- Adds it to the selected microphone's endpoint effect slot (`MMDevices\Audio\Capture\{id}\FxProperties`)
- If present, removes the link to Windows' "Voice Clarity" effect pack for that microphone (it blocks third-party effects); it is restored on uninstall
- Sets `DisableProtectedAudioDG = 1` (legacy audio setting)
- Restarts the Windows Audio services
- Backs up every original value under `HKLM\SOFTWARE\zSound` for the uninstaller

### How it works
- `native/zsapo.cpp` — a tiny Audio Processing Object (APO) loaded by Windows (`audiodg.exe`) into the microphone's effect chain. It mixes zSound's audio into the mic signal in real time (no allocations, locks or system calls on the audio thread).
- `Audio/MicBus.cs` — zSound writes the mixed sounds into a shared-memory ring buffer, paced to the plugin's read position so the two audio clocks never drift apart.
- `Core/MicApo.cs` — install / verify / rollback / uninstall, running as a separate elevated `zSound.exe --apo …` process.

### Build from source
Requirements: .NET 8 SDK, Zig 0.13 (`zig` on PATH, `$env:ZIG`, or `pip install ziglang==0.13.0`).
```powershell
.\build.ps1            # -> dist\zSound.exe
.\build.ps1 -DebugLog  # plugin writes %ProgramData%\zSound\apo.log (development only)
```
`native/probe_com.py` is a quick COM smoke test for the plugin DLL.

### Security notes
- The plugin only reads audio from the shared memory of the user signed in at the console; other user sessions and low-integrity (sandboxed) processes cannot inject audio.
- Release builds of the plugin write no files.
- The elevated helper never writes into user-controlled paths except creating a new result file.

### Known limitations
- If your microphone driver already uses its own endpoint effect, the installer refuses instead of overwriting it.
- Apps that open the microphone in "raw" mode may bypass effects.
- The plugin is not signed by Microsoft; Windows loads it like other community audio plugins (e.g. Equalizer APO).

### Contributing
Issues and pull requests are welcome. Licensed under the [MIT License](LICENSE).

---

## Türkçe

### Özellikler
- Sesler **gerçek mikrofonundan** çıkar (Soundpad'in kullandığı yöntemle, küçük bir Windows ses eklentisi sayesinde). Sanal kablo yok, varsayılan mikrofon değişmez.
- Kendi sesin normal gitmeye devam eder; istersen ses çalarken mikrofonun kısılır (**ducking**)
- Aynı anda birden fazla ses; limiter sayesinde patlama/cızırtı olmaz
- Global kısayollar (`Num1+Num2` gibi kombinasyonlar, fare yan tuşları), basılı tuttukça çal, döngü
- Dalga formlu kırpma ekranı, fade in/out, hız/perde, ses başına seviye, ses eşitleme
- "Onlar" (mikrofon) ve "ben" (kulaklık) için ayrı ses seviyesi
- Ses çalarken bas-konuş tuşunu otomatik basılı tutma
- Ses **ve video** dosyalarını sürükle bırak (mp3, wav, ogg, flac, m4a, mp4, mkv, webm…)
- Tepsi modu, Windows ile başlama, boştayken çok düşük CPU/RAM

### Gereksinimler
- Windows 10 veya 11, 64-bit
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)
- Mikrofon eklentisini kurmak için bir kez yönetici izni

### Kurulum ve ilk kullanım
1. [Releases](../../releases) sayfasından `zSound.exe`'yi indir ve çalıştır.
2. **Ayarlar** (dişli simgesi) → *CİHAZLAR* altında:
   - **Senin duyduğun** = kulaklığın/hoparlörün
   - **Gerçek mikrofonun** = gerçek mikrofonun
3. *MİKROFON* → **Mikrofon eklentisi** → **Kur**'a bas, yönetici iznine **Evet** de.
   - Windows sesi birkaç saniye kesilir.
   - Mikrofona **3 saniyelik bir test sesi** gönderilip ölçülür. Ses geçmezse her şey otomatik geri alınır.
4. Durum **"Etkin"** olduğunda çaldığın sesler mikrofonundan gider.

### Kullanım
- Dosyaları pencereye sürükle (ya da **+**).
- Çift tık / Enter ile çal. Sağ tık: düzenle, kırp, kısayol ata.
- Alt çubuk: **ONLAR** = karşı tarafın duyduğu seviye, **BEN** = senin duyduğun seviye.
- Komut satırı (zSound açıkken): `zSound.exe --play "isim"`, `--stop`, `--pause`, `--random`, `--add "dosya"`.

### Discord ayarları (önemli)
Discord sesleri senin sesin gibi işler ve kesebilir. *Kullanıcı Ayarları → Ses ve Görüntü*:
- **Yankı engelleme: KAPALI** (en önemlisi; yoksa ses kulaklığında da çaldığı için Discord onu yankı sanıp siler)
- **Gürültü azaltma: Yok**
- **Otomatik kazanç kontrolü: KAPALI**
- **Bas-Konuş** kullan ya da otomatik giriş hassasiyetini kapatıp eşiği düşür.

### Kaldırma
Ayarlar → **Mikrofon eklentisi → Kaldır**. Kurulumun değiştirdiği her kayıt defteri değeri birebir eski haline döner, eklenti dosyaları silinir. Sonra `zSound.exe`'yi ve `%AppData%\zSound` klasörünü (ayarların) silebilirsin.

### Kurulum neyi değiştirir (şeffaflık)
- `zsapo.dll`'i `C:\Program Files\zSound\` içine kopyalar
- COM sınıfı ve ses işleme nesnesi (APO) olarak kaydeder (`HKLM\SOFTWARE\Classes`)
- Seçilen mikrofonun uç nokta efekt yuvasına ekler (`MMDevices\Audio\Capture\{id}\FxProperties`)
- Varsa o mikrofonun Windows "Voice Clarity" efekt paketi bağlantısını kaldırır (üçüncü taraf efektleri engelliyor); kaldırınca geri konur
- `DisableProtectedAudioDG = 1` yapar (eski bir ses ayarı)
- Windows ses hizmetlerini yeniden başlatır
- Tüm orijinal değerleri kaldırıcı için `HKLM\SOFTWARE\zSound` altına yedekler

### Nasıl çalışır
- `native/zsapo.cpp` — Windows'un (`audiodg.exe`) mikrofonun efekt zincirine yüklediği küçük bir APO. zSound'un seslerini gerçek zamanlı olarak mikrofon sinyaline karıştırır (ses thread'inde bellek ayırma, kilit ya da sistem çağrısı yok).
- `Audio/MicBus.cs` — zSound karışık sesleri paylaşımlı bellekteki halka tampona yazar; yazma hızı eklentinin okuma yerine kilitlidir, iki ses saati arasında kayma birikmez.
- `Core/MicApo.cs` — kurulum / doğrulama / geri alma / kaldırma; ayrı, yönetici yetkili bir `zSound.exe --apo …` sürecinde çalışır.

### Kaynaktan derleme
Gereksinimler: .NET 8 SDK, Zig 0.13 (`zig` PATH'te, `$env:ZIG` ya da `pip install ziglang==0.13.0`).
```powershell
.\build.ps1            # -> dist\zSound.exe
.\build.ps1 -DebugLog  # eklenti %ProgramData%\zSound\apo.log'a yazar (sadece geliştirme)
```

### Bilinen sınırlamalar
- Mikrofon sürücüsü kendi uç nokta efektini kullanıyorsa kurulum üzerine yazmaz, durur.
- Mikrofonu "ham" (raw) modda açan uygulamalar efektleri atlayabilir.
- Eklenti Microsoft imzalı değildir; Windows onu diğer topluluk ses eklentileri (ör. Equalizer APO) gibi yükler.

### Katkı
Issue ve pull request'ler açıktır. [MIT Lisansı](LICENSE) ile lisanslanmıştır.
