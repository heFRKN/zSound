using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using static zSound.Core.Native;

namespace zSound.Core
{
    /// Global klavye/fare kancası. Tuşları yutmaz, oyunda da çalışır, bırakmayı da yakalar.
    public sealed class Hotkeys : IDisposable
    {
        IntPtr kbHook, msHook;
        readonly HookProc kbProc, msProc;
        readonly Dispatcher ui;
        readonly bool[] down = new bool[256];

        /// (hotkey, isDown) — UI thread'inde çağrılır
        public event Action<Hotkey, bool> Key;
        /// tuş atama modu: bir sonraki tuş buraya gelir
        public Action<Hotkey> Capture;

        public Hotkeys(Dispatcher ui)
        {
            this.ui = ui;
            kbProc = KbHook;
            msProc = MsHook;
            kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, GetModuleHandle(null), 0);
        }

        /// fare kancası sadece bir kısayol fare tuşu kullanıyorsa açılır (fare hareketi CPU yemesin)
        public void EnableMouse(bool on)
        {
            if (on && msHook == IntPtr.Zero) msHook = SetWindowsHookEx(WH_MOUSE_LL, msProc, GetModuleHandle(null), 0);
            else if (!on && msHook != IntPtr.Zero) { UnhookWindowsHookEx(msHook); msHook = IntPtr.Zero; }
        }

        static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

        // basılı (modifier olmayan) tuşlar, basılma sırasıyla — kombinasyon için
        readonly System.Collections.Generic.List<int> held = new System.Collections.Generic.List<int>();
        Hotkey captured; // tuş atama: bırakılana kadar en son kombinasyon

        static Hotkey Build(int vk, int vk2) => new Hotkey
        {
            Vk = vk,
            Vk2 = vk2,
            Ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0,
            Alt = (GetAsyncKeyState(0x12) & 0x8000) != 0,
            Shift = (GetAsyncKeyState(0x10) & 0x8000) != 0,
            Win = ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0
        };

        void Handle(int vk, bool isDown)
        {
            if (vk <= 0 || vk > 255) return;
            if (isDown)
            {
                if (down[vk]) return; // otomatik tekrar
                down[vk] = true;
            }
            else
            {
                if (!down[vk]) return;
                down[vk] = false;
            }
            if (IsModifier(vk)) return;

            Hotkey hk;
            if (isDown)
            {
                hk = Build(vk, held.Count > 0 ? held[^1] : 0);
                held.Add(vk);
            }
            else
            {
                held.Remove(vk);
                hk = Build(vk, 0);
            }

            ui.BeginInvoke(DispatcherPriority.Send, () =>
            {
                if (Capture != null)
                {
                    // basarken kaydet, ilk bırakışta bitir → Num1 basılıyken Num2 = "Num1+Num2"
                    if (isDown) captured = hk;
                    else if (captured != null) { var c = Capture; var h = captured; Capture = null; captured = null; c(h); }
                    return;
                }
                captured = null;
                Key?.Invoke(hk, isDown);
            });
        }

        IntPtr KbHook(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
                if ((k.flags & 0x10) == 0) // kendi gönderdiğimiz (injected) tuşları yok say
                {
                    int msg = (int)w;
                    Handle((int)k.vkCode, msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                }
            }
            return CallNextHookEx(kbHook, code, w, l);
        }

        IntPtr MsHook(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                int msg = (int)w;
                if (msg >= WM_MBUTTONDOWN && msg <= WM_XBUTTONUP)
                {
                    if (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP) Handle(0x04, msg == WM_MBUTTONDOWN);
                    else if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                    {
                        var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                        int xb = (int)(m.mouseData >> 16);
                        Handle(xb == 1 ? 0x05 : 0x06, msg == WM_XBUTTONDOWN);
                    }
                }
            }
            return CallNextHookEx(msHook, code, w, l);
        }

        public void Dispose()
        {
            if (kbHook != IntPtr.Zero) UnhookWindowsHookEx(kbHook);
            EnableMouse(false);
            kbHook = IntPtr.Zero;
        }
    }
}
