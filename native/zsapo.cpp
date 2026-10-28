// zSound mikrofon eklentisi (APO).
// Windows ses motoru (audiodg.exe) bunu mikrofonun "ses iyileştirmeleri" zincirinde çalıştırır.
// zSound'un paylaşımlı belleğe yazdığı sesleri gerçek mikrofon sinyaline karıştırır.
// Gerçek zamanlı thread'de çalışır: APOProcess içinde bellek ayırma, kilit, sistem çağrısı yok.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <mmreg.h>
#include <sddl.h>
#include <string.h>

// C++ çalışma zamanı olmadan: new/delete doğrudan Windows heap'i
void* operator new(size_t n) { return HeapAlloc(GetProcessHeap(), 0, n); }
void operator delete(void* p) noexcept { if (p) HeapFree(GetProcessHeap(), 0, p); }
void operator delete(void* p, size_t) noexcept { if (p) HeapFree(GetProcessHeap(), 0, p); }
extern "C" int __cxa_pure_virtual() { return 0; }

// ───────── APO tipleri (audioenginebaseapo.h / audiomediatype.h ile aynı yerleşim) ─────────

typedef LONGLONG HNSTIME;

enum APO_CONNECTION_BUFFER_TYPE { APO_CONNECTION_BUFFER_TYPE_ALLOCATED = 0, APO_CONNECTION_BUFFER_TYPE_EXTERNAL = 1, APO_CONNECTION_BUFFER_TYPE_DEPENDANT = 2 };
enum APO_BUFFER_FLAGS { BUFFER_INVALID = 0, BUFFER_VALID = 1, BUFFER_SILENT = 2 };
enum APO_FLAG { APO_FLAG_NONE = 0, APO_FLAG_INPLACE = 1, APO_FLAG_SAMPLESPERFRAME_MUST_MATCH = 2,
                APO_FLAG_FRAMESPERSECOND_MUST_MATCH = 4, APO_FLAG_BITSPERSAMPLE_MUST_MATCH = 8, APO_FLAG_DEFAULT = 0xE };

struct UNCOMPRESSEDAUDIOFORMAT
{
    GUID guidFormatType;
    DWORD dwSamplesPerFrame;
    DWORD dwBytesPerSampleContainer;
    DWORD dwValidBitsPerSample;
    FLOAT fFramesPerSecond;
    DWORD dwChannelMask;
};

struct IAudioMediaType : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* compressed) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType* other, DWORD* flags) = 0;
    virtual const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* fmt) = 0;
};

struct APO_CONNECTION_DESCRIPTOR
{
    APO_CONNECTION_BUFFER_TYPE Type;
    UINT_PTR pBuffer;
    UINT32 u32MaxFrameCount;
    IAudioMediaType* pFormat;
    UINT32 u32Signature;
};

struct APO_CONNECTION_PROPERTY
{
    UINT_PTR pBuffer;
    UINT32 u32ValidFrameCount;
    APO_BUFFER_FLAGS u32BufferFlags;
    UINT32 u32Signature;
};

struct APO_REG_PROPERTIES
{
    CLSID clsid;
    APO_FLAG Flags;
    WCHAR szFriendlyName[256];
    WCHAR szCopyrightInfo[256];
    UINT32 u32MajorVersion;
    UINT32 u32MinorVersion;
    UINT32 u32MinInputConnections;
    UINT32 u32MaxInputConnections;
    UINT32 u32MinOutputConnections;
    UINT32 u32MaxOutputConnections;
    UINT32 u32MaxInstances;
    UINT32 u32NumAPOInterfaces;
    IID iidAPOInterfaceList[1];
};

struct IAudioProcessingObject : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE Reset() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* t) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** props) = 0;
    virtual HRESULT STDMETHODCALLTYPE Initialize(UINT32 cbDataSize, BYTE* data) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) = 0;
    virtual HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* count) = 0;
};

struct IAudioProcessingObjectRT : public IUnknown
{
    virtual void STDMETHODCALLTYPE APOProcess(UINT32 nIn, APO_CONNECTION_PROPERTY** in, UINT32 nOut, APO_CONNECTION_PROPERTY** out) = 0;
    virtual UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 outFrames) = 0;
    virtual UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 inFrames) = 0;
};

struct IAudioProcessingObjectConfiguration : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 nIn, APO_CONNECTION_DESCRIPTOR** in, UINT32 nOut, APO_CONNECTION_DESCRIPTOR** out) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnlockForProcess() = 0;
};

struct IAudioSystemEffects : public IUnknown {};

static const GUID IID_IAudioProcessingObject_ = { 0xFD7F2B29, 0x24D0, 0x4B5C, { 0xB1, 0x77, 0x59, 0x2C, 0x39, 0xF9, 0xCA, 0x10 } };
static const GUID IID_IAudioProcessingObjectRT_ = { 0x9E1D6A6D, 0xDDBC, 0x4E95, { 0xA4, 0xC7, 0xAD, 0x64, 0xBA, 0x37, 0x84, 0x6C } };
static const GUID IID_IAudioProcessingObjectConfiguration_ = { 0x0E5ED805, 0xABA6, 0x49C3, { 0x8F, 0x9A, 0x2B, 0x8C, 0x88, 0x9C, 0x4F, 0xA8 } };
static const GUID IID_IAudioSystemEffects_ = { 0x5FA00F27, 0xADD6, 0x499A, { 0x8A, 0x9D, 0x6B, 0x98, 0x52, 0x1F, 0xA7, 0x5B } };
static const GUID IID_IUnknown_ = { 0x00000000, 0x0000, 0x0000, { 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 } };
static const GUID IID_IClassFactory_ = { 0x00000001, 0x0000, 0x0000, { 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 } };
static const GUID SUBTYPE_FLOAT = { 0x00000003, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 } };

// zSound APO kimliği — C# tarafındaki MicApo.Clsid ile aynı olmalı
// {5A7C3E91-2B4D-4F6A-9E1C-7D3B8A2F4C60}
static const GUID CLSID_ZSoundApo = { 0x5A7C3E91, 0x2B4D, 0x4F6A, { 0x9E, 0x1C, 0x7D, 0x3B, 0x8A, 0x2F, 0x4C, 0x60 } };

#define APOERR_FORMAT_NOT_SUPPORTED ((HRESULT)0x887D0003L)

static LONG g_objects = 0, g_locks = 0, g_nextId = 0;

// ───────── teşhis günlüğü (sadece geliştirici derlemesi: -DZS_LOG) ─────────
// %ProgramData%\zSound\apo.log'a yazar. Yayın derlemesinde kapalı: ses motoru (LocalService)
// herkesin yazabildiği bir klasöre dosya yazmasın. Sadece gerçek zamanlı olmayan çağrılarda kullanılır.

#ifndef ZS_LOG
static inline void Log(const char*, long long = 0, long long = 0) {}
static inline HRESULT LogCom(const char*, REFGUID, HRESULT hr) { return hr; }
#else
static void Log(const char* msg, long long a = 0, long long b = 0)
{
    WCHAR path[MAX_PATH];
    if (!ExpandEnvironmentStringsW(L"%ProgramData%\\zSound\\apo.log", path, MAX_PATH)) return;
    HANDLE f = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER size;
    if (GetFileSizeEx(f, &size) && size.QuadPart > 256 * 1024) { CloseHandle(f); return; }
    SYSTEMTIME t;
    GetLocalTime(&t);
    char line[256];
    int n = 0;
    auto put = [&](const char* s) { while (*s && n < 240) line[n++] = *s++; };
    auto num = [&](long long v) {
        char d[24]; int k = 0;
        bool neg = v < 0; unsigned long long u = neg ? 0ULL - (unsigned long long)v : (unsigned long long)v;
        do { d[k++] = (char)('0' + u % 10); u /= 10; } while (u);
        if (neg && n < 240) line[n++] = '-';
        while (k && n < 240) line[n++] = d[--k];
    };
    num(t.wHour); put(":"); num(t.wMinute); put(":"); num(t.wSecond); put(" pid="); num(GetCurrentProcessId()); put(" ");
    put(msg); put(" "); num(a); put(" "); num(b); put("\r\n");
    DWORD w;
    WriteFile(f, line, n, &w, nullptr);
    CloseHandle(f);
}

// COM diagnostics only: never call from APOProcess or under the loader lock.
static HRESULT LogCom(const char* call, REFGUID guid, HRESULT hr)
{
    char line[160];
    int n = 0;
    while (*call && n < 100) line[n++] = *call++;
    line[n++] = ' '; line[n++] = '{';
    auto hex = [&](unsigned long long v, int digits) {
        for (int i = digits - 1; i >= 0; --i)
            line[n++] = "0123456789ABCDEF"[(v >> (i * 4)) & 15];
    };
    hex(guid.Data1, 8); line[n++] = '-';
    hex(guid.Data2, 4); line[n++] = '-';
    hex(guid.Data3, 4); line[n++] = '-';
    for (int i = 0; i < 8; ++i) { if (i == 2) line[n++] = '-'; hex(guid.Data4[i], 2); }
    line[n++] = '}'; line[n++] = ' '; line[n++] = 'h'; line[n++] = 'r'; line[n++] = '=';
    hex((DWORD)hr, 8); line[n] = 0;
    Log(line, GetCurrentThreadId());
    return hr;
}
#endif

// ───────── paylaşımlı bellek (zSound yazar, her APO örneği kendi imleciyle okur) ─────────

// Belleği zSound kendi oturumunda (Local\) oluşturur, APO "Session\<n>\" ile açar.
// audiodg Global\ nesne oluşturamaz: Audiosrv'nin token'ında SeCreateGlobalPrivilege yok.
#define BUS_BASENAME L"zSoundMicBus"
#ifdef ZS_TEST
#define BUS_TESTNAME L"Local\\zSoundMicBusTest"   // test: aynı oturumda
#endif
#define BUS_MAGIC 0x7A534E44u   // 'zSND'
#define BUS_VERSION 2           // zSound MicBus.cs ile aynı
#define BUS_FRAMES 16384u       // 2^14 stereo frame (~340ms @48k)

#pragma pack(push, 8)
struct Bus
{
    UINT32 magic, version;
    UINT32 rate, channels;          // zSound'un yazdığı format (stereo float)
    UINT32 capacity, reserved;
    volatile LONGLONG writePos;     // yazılan toplam frame
    volatile LONG soundActive;      // şu an ses çalıyor mu (ducking için)
    volatile float micGain;         // gerçek mikrofon kazancı
    volatile float duckLevel;       // ses çalarken mikrofon çarpanı
    volatile LONG micMuted;
    volatile LONG readers;          // bağlı APO örneği sayısı (bilgi)
    volatile LONGLONG heartbeat;    // zSound'un son canlılık zamanı (GetTickCount64)
    volatile LONGLONG readHead;     // APO'nun okuduğu yer — zSound yazmayı buna göre ayarlar (kesinti olmasın)
    volatile LONG owner;            // sesleri karıştıran APO örneği (aynı zincirde SFX+MFX+EFX olursa sesler iki kez eklenmesin)
    LONG pad;
    volatile LONGLONG ownerBeat;    // sahibin son işleme zamanı; 200ms susarsa başkası devralır
    float data[BUS_FRAMES * 2];
};
#pragma pack(pop)

// ───────── APO ─────────

class ZApo : public IAudioProcessingObject, public IAudioProcessingObjectRT,
             public IAudioProcessingObjectConfiguration, public IAudioSystemEffects
{
    LONG ref = 1;
    UINT32 channels = 0;
    float rate = 48000;
    HANDLE map = nullptr;
    Bus* volatile bus = nullptr;     // açıcı thread yayınlar, ses thread'i sadece okur
    HANDLE opener = nullptr, stop = nullptr;
    DWORD lastErr = 0;
    LONGLONG readPos = 0;
    double frac = 0;
    float duck = 1.f;
    bool owning = false;
    const LONG id = (LONG)((GetCurrentProcessId() << 12) | (InterlockedIncrement(&g_nextId) & 0xFFF));   // süreçler arası da benzersiz

public:
    explicit ZApo(IUnknown* o)
    {
        inner.self = this;
        outer = o ? o : static_cast<IUnknown*>(&inner);
        InterlockedIncrement(&g_objects);
    }
    ~ZApo() { Close(); InterlockedDecrement(&g_objects); }

    // zSound'un belleğini dener. Sadece ekrandaki (konsol) oturum: aynı bilgisayardaki başka bir
    // kullanıcının oturumu bu mikrofona ses basamasın.
    bool TryOpen()
    {
#ifdef ZS_TEST
        HANDLE m = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, BUS_TESTNAME);
#else
        HANDLE m = nullptr;
        DWORD s = WTSGetActiveConsoleSessionId();
        if (s != 0xFFFFFFFF)
        {
            // "Session\<s>\zSoundMicBus" (user32'ye bağlanmamak için elle)
            WCHAR name[64] = L"Session\\", digits[12];
            int n = 0, k = 8;
            do { digits[n++] = (WCHAR)(L'0' + s % 10); s /= 10; } while (s);
            while (n) name[k++] = digits[--n];
            name[k++] = L'\\';
            name[k] = 0;
            wcscat_s(name, BUS_BASENAME);
            m = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
        }
#endif
        if (!m)
        {
            DWORD e = GetLastError();
            if (e != lastErr) { lastErr = e; Log("bus acilamadi err/konsol-oturum", e, (long long)WTSGetActiveConsoleSessionId()); }
            return false;
        }
        Bus* b = (Bus*)MapViewOfFile(m, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(Bus));
        if (!b || b->magic != BUS_MAGIC || b->version != BUS_VERSION || b->capacity != BUS_FRAMES)
        {
            DWORD e = b ? 1 : GetLastError();
            if (e != lastErr) { lastErr = e; Log("bus eslenemedi/gecersiz err", e); }
            if (b) UnmapViewOfFile(b);
            CloseHandle(m);
            return false;
        }
        Log("bus baglandi okuyucu", b->readers + 1);
        lastErr = 0;
        map = m;
        InterlockedIncrement(&b->readers);
        readPos = b->writePos;
        frac = 0;
        MemoryBarrier();
        bus = b;
        return true;
    }

    static DWORD WINAPI OpenerThread(LPVOID p)
    {
        auto self = (ZApo*)p;
        // zSound sonradan açılabilir: bulana kadar yarım saniyede bir dene
        while (!self->TryOpen())
            if (WaitForSingleObject(self->stop, 500) == WAIT_OBJECT_0) break;
        return 0;
    }

    void Open()
    {
        if (opener || bus) return;
        stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (stop) opener = CreateThread(nullptr, 0, OpenerThread, this, 0, nullptr);
    }

    void Close()
    {
        if (opener) { SetEvent(stop); WaitForSingleObject(opener, INFINITE); CloseHandle(opener); opener = nullptr; }
        if (stop) { CloseHandle(stop); stop = nullptr; }
        Bus* b = bus;
        bus = nullptr;
        if (b)
        {
            InterlockedCompareExchange(&b->owner, 0, id);
            InterlockedDecrement(&b->readers);
            UnmapViewOfFile(b);
        }
        if (map) { CloseHandle(map); map = nullptr; }
    }

    // ───── COM aggregation ─────
    // Ses motoru APO'yu kendi nesnesinin içine gömerek oluşturur (CreateInstance'a outer verir,
    // IID_IUnknown ister). Ömrü bu iç IUnknown yönetir; arayüzlerdeki IUnknown çağrıları dış
    // nesneye (outer) gider. Gömülmeden oluşturulunca outer = iç nesnenin kendisi.
    struct Inner : public IUnknown
    {
        ZApo* self = nullptr;
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** pv) override
        {
            if (!pv) return LogCom("APO.inner.QI", iid, E_POINTER);
            if (iid == IID_IUnknown_) { *pv = static_cast<IUnknown*>(this); AddRef(); return LogCom("APO.inner.QI", iid, S_OK); }
            return self->QueryOwn(iid, pv);
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&self->ref); }
        ULONG STDMETHODCALLTYPE Release() override
        {
            LONG r = InterlockedDecrement(&self->ref);
            if (r == 0) delete self;
            return r;
        }
    } inner;
    IUnknown* outer = nullptr;

    HRESULT QueryOwn(REFIID iid, void** pv)
    {
        if (iid == IID_IAudioProcessingObject_) *pv = static_cast<IAudioProcessingObject*>(this);
        else if (iid == IID_IAudioProcessingObjectRT_) *pv = static_cast<IAudioProcessingObjectRT*>(this);
        else if (iid == IID_IAudioProcessingObjectConfiguration_) *pv = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (iid == IID_IAudioSystemEffects_) *pv = static_cast<IAudioSystemEffects*>(this);
        else { *pv = nullptr; return LogCom("APO.QI", iid, E_NOINTERFACE); }
        static_cast<IUnknown*>(*pv)->AddRef();   // dış nesneye gider (gömülüyse)
        return LogCom("APO.QI", iid, S_OK);
    }

    // arayüzlerin IUnknown'u: hepsi dış nesneye
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** pv) override { return outer->QueryInterface(iid, pv); }
    ULONG STDMETHODCALLTYPE AddRef() override { return outer->AddRef(); }
    ULONG STDMETHODCALLTYPE Release() override { return outer->Release(); }

    // IAudioProcessingObject
    HRESULT STDMETHODCALLTYPE Reset() override { if (bus) { readPos = bus->writePos; frac = 0; } return S_OK; }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* t) override { if (!t) return E_POINTER; *t = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* c) override { if (!c) return E_POINTER; *c = channels; return S_OK; }
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 cb, BYTE*) override { Log("initialize bytes", cb); return S_OK; }

    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** props) override
    {
        if (!props) return E_POINTER;
        auto p = (APO_REG_PROPERTIES*)CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES));
        if (!p) return E_OUTOFMEMORY;
        memset(p, 0, sizeof(*p));
        p->clsid = CLSID_ZSoundApo;
        p->Flags = (APO_FLAG)(APO_FLAG_INPLACE | APO_FLAG_DEFAULT);
        wcscpy_s(p->szFriendlyName, L"zSound Mic");
        wcscpy_s(p->szCopyrightInfo, L"zSound");
        p->u32MajorVersion = 1;
        p->u32MinInputConnections = p->u32MaxInputConnections = 1;
        p->u32MinOutputConnections = p->u32MaxOutputConnections = 1;
        p->u32MaxInstances = 0xFFFFFFFF;
        p->u32NumAPOInterfaces = 1;
        p->iidAPOInterfaceList[0] = IID_IAudioProcessingObject_;
        *props = p;
        return S_OK;
    }

    static bool IsFloat(IAudioMediaType* t)
    {
        if (!t) return false;
        UNCOMPRESSEDAUDIOFORMAT f;
        if (FAILED(t->GetUncompressedAudioFormat(&f))) return false;
        return f.guidFormatType == SUBTYPE_FLOAT && f.dwBytesPerSampleContainer == 4;
    }

    // float değilse aynı hız/kanalda float öner (S_FALSE) — ses motoru araya çevirici koyar
    HRESULT Supported(IAudioMediaType* requested, IAudioMediaType** supported, int dir)
    {
        if (!requested || !supported) return E_POINTER;
        *supported = nullptr;
        UNCOMPRESSEDAUDIOFORMAT f;
        if (FAILED(requested->GetUncompressedAudioFormat(&f))) { Log("format: okunamadi", dir); return APOERR_FORMAT_NOT_SUPPORTED; }
        Log(dir ? "format out ch/rate" : "format in ch/rate", f.dwSamplesPerFrame, (long long)f.fFramesPerSecond);
        if (IsFloat(requested))
        {
            requested->AddRef();
            *supported = requested;
            return S_OK;
        }
        typedef HRESULT(WINAPI* CreateFn)(const UNCOMPRESSEDAUDIOFORMAT*, IAudioMediaType**);
        static CreateFn create = nullptr;
        if (!create)
        {
            HMODULE mf = LoadLibraryW(L"mfplat.dll");
            if (mf) create = (CreateFn)GetProcAddress(mf, "CreateAudioMediaTypeFromUncompressedAudioFormat");
        }
        Log("format: float degil, bit", f.dwValidBitsPerSample, create != nullptr);
        if (!create) return APOERR_FORMAT_NOT_SUPPORTED;
        f.guidFormatType = SUBTYPE_FLOAT;
        f.dwBytesPerSampleContainer = 4;
        f.dwValidBitsPerSample = 32;
        if (FAILED(create(&f, supported)) || !*supported) { *supported = nullptr; return APOERR_FORMAT_NOT_SUPPORTED; }
        return S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType*, IAudioMediaType* r, IAudioMediaType** s) override { return Supported(r, s, 0); }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType*, IAudioMediaType* r, IAudioMediaType** s) override { return Supported(r, s, 1); }

    // IAudioProcessingObjectConfiguration
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 nIn, APO_CONNECTION_DESCRIPTOR** in, UINT32 nOut, APO_CONNECTION_DESCRIPTOR** out) override
    {
        if (nIn != 1 || nOut != 1 || !in || !out || !in[0] || !in[0]->pFormat) return E_INVALIDARG;
        UNCOMPRESSEDAUDIOFORMAT f;
        if (FAILED(in[0]->pFormat->GetUncompressedAudioFormat(&f))) return E_INVALIDARG;
        channels = f.dwSamplesPerFrame;
        rate = f.fFramesPerSecond > 0 ? f.fFramesPerSecond : 48000.f;
        Log("lock ch/rate", channels, (long long)rate);
        Open();
        duck = 1.f;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override { Log("unlock"); Close(); return S_OK; }

    // IAudioProcessingObjectRT
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 n) override { return n; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 n) override { return n; }

    void STDMETHODCALLTYPE APOProcess(UINT32 nIn, APO_CONNECTION_PROPERTY** in, UINT32 nOut, APO_CONNECTION_PROPERTY** out) override
    {
        if (nIn < 1 || nOut < 1) return;
        APO_CONNECTION_PROPERTY* ip = in[0];
        APO_CONNECTION_PROPERTY* op = out[0];
        UINT32 frames = ip->u32ValidFrameCount;
        float* src = (float*)ip->pBuffer;
        float* dst = (float*)op->pBuffer;
        UINT32 ch = channels ? channels : 1;
        UINT32 n = frames * ch;
        bool silent = ip->u32BufferFlags == BUFFER_SILENT;

        if (silent) memset(dst, 0, n * sizeof(float));
        else if (dst != src) memcpy(dst, src, n * sizeof(float));

        Bus* b = bus;
        bool added = false;
        // tek sahip: diğer örnekler sesi olduğu gibi geçirir (çift karışma / çift kısma olmasın)
        if (b && b->magic == BUS_MAGIC)
        {
            ULONGLONG now = GetTickCount64();
            LONG o = b->owner;
            if (o != id && (o == 0 || now - (ULONGLONG)b->ownerBeat > 200))
                InterlockedCompareExchange(&b->owner, id, o);
            if (b->owner == id)
            {
                b->ownerBeat = (LONGLONG)now;
                // sahipliği yeni aldıysak tampondaki eski sesi çalma
                if (!owning) { readPos = b->writePos; frac = 0; owning = true; }
            }
            else { b = nullptr; owning = false; }
        }
        if (b)
        {
            // kendi sesin: kazanç / sustur / ses çalarken kıs
            // zSound 3 sn'dir ses vermiyorsa (kapandı/çöktü) mikrofona hiç dokunma
            bool alive = GetTickCount64() - (ULONGLONG)b->heartbeat < 3000;
            float target = !alive ? 1.f : b->micMuted ? 0.f : b->micGain * (b->soundActive ? b->duckLevel : 1.f);
            if (!silent && (target != 1.f || duck != 1.f))
            {
                float coef = target < duck ? 0.02f : 0.002f;
                for (UINT32 i = 0; i < frames; i++)
                {
                    duck += (target - duck) * coef;
                    for (UINT32 c = 0; c < ch; c++) dst[i * ch + c] *= duck;
                }
            }
            else duck = target;

            // zSound sesleri: halka tampondan oku, hız farkını lineer çevir
            LONGLONG w = b->writePos;
            MemoryBarrier();
            LONGLONG avail = w - readPos;
            LONGLONG maxLag = (LONGLONG)(b->rate / 8);      // 125ms'den fazla geride kalırsak
            if (avail > maxLag || avail < 0) { readPos = w - (LONGLONG)(b->rate / 40); frac = 0; } // 25ms'e çek
            double step = (double)b->rate / rate;
            const UINT32 mask = BUS_FRAMES - 1;
            for (UINT32 i = 0; i < frames; i++)
            {
                if (readPos + 1 >= w) break;
                const float* a = &b->data[(readPos & mask) * 2];
                const float* c = &b->data[((readPos + 1) & mask) * 2];
                float t = (float)frac;
                float l = a[0] + (c[0] - a[0]) * t;
                float r = a[1] + (c[1] - a[1]) * t;
                if (ch == 1) dst[i] += (l + r) * 0.5f;
                else { dst[i * ch] += l; dst[i * ch + 1] += r; }
                added = true;
                frac += step;
                while (frac >= 1.0) { frac -= 1.0; readPos++; }
            }
            b->readHead = readPos;
        }

        op->u32ValidFrameCount = frames;
        op->u32BufferFlags = (silent && !added) ? BUFFER_SILENT : BUFFER_VALID;
    }
};

// ───────── COM fabrikası ve DLL girişleri ─────────

class Factory : public IClassFactory
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** pv) override
    {
        if (!pv) return LogCom("Factory.QI", iid, E_POINTER);
        if (iid == IID_IUnknown_ || iid == IID_IClassFactory_)
        {
            *pv = this; AddRef(); return LogCom("Factory.QI", iid, S_OK);
        }
        *pv = nullptr;
        return LogCom("Factory.QI", iid, E_NOINTERFACE);
    }
    // The factory is static, but outstanding references must prevent DLL unload.
    ULONG STDMETHODCALLTYPE AddRef() override { InterlockedIncrement(&g_objects); return 2; }
    ULONG STDMETHODCALLTYPE Release() override { InterlockedDecrement(&g_objects); return 1; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** pv) override
    {
        LogCom("Factory.CreateInstance enter", iid, S_OK);
        if (!pv) return LogCom("Factory.CreateInstance", iid, E_POINTER);
        *pv = nullptr;
        // ses motoru gömerek oluşturur: o zaman yalnızca IUnknown istenebilir (COM kuralı)
        if (outer && iid != IID_IUnknown_) return LogCom("Factory.CreateInstance", iid, CLASS_E_NOAGGREGATION);
        Log("nesne olusturuluyor, gomulu", outer != nullptr);
        auto o = new ZApo(outer);
        if (!o) return LogCom("Factory.CreateInstance", iid, E_OUTOFMEMORY);
        HRESULT hr = o->inner.QueryInterface(iid, pv);   // gömülüyse iç IUnknown döner
        o->inner.Release();
        return LogCom("Factory.CreateInstance", iid, hr);
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

static Factory g_factory;

extern "C" __declspec(dllexport) HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** pv)
{
    LogCom("DllGetClassObject CLSID", clsid, S_OK);
    LogCom("DllGetClassObject IID", iid, S_OK);
    if (!pv) return LogCom("DllGetClassObject return", iid, E_POINTER);
    *pv = nullptr;
    if (clsid != CLSID_ZSoundApo) return LogCom("DllGetClassObject return", iid, CLASS_E_CLASSNOTAVAILABLE);
    HRESULT hr = g_factory.QueryInterface(iid, pv);
    return LogCom("DllGetClassObject return", iid, hr);
}

extern "C" __declspec(dllexport) HRESULT __stdcall DllCanUnloadNow()
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(h);
    return TRUE;
}
