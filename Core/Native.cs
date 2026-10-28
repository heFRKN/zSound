using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace zSound.Core
{
    static class Native
    {
        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        public const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;
        public const int WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;

        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

        // Bas-konuş tuşu: oyunlar scancode okuduğu için ikisini de gönderiyoruz
        public static void SendKey(int vk, bool down)
        {
            if (vk <= 0) return;
            var scan = (ushort)MapVirtualKey((uint)vk, 0);
            uint flags = 0x0008; // SCANCODE
            if (!down) flags |= 0x0002;
            if (vk is >= 0x21 and <= 0x2E or 0xA3 or 0xA5) flags |= 0x0001; // extended
            var inp = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = flags } };
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
        }

        // DWM: koyu başlık + akrilik arka plan
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

        public static bool ApplyBackdrop(IntPtr hwnd)
        {
            // cam çerçeve uzatılınca DWM kendi başlık butonlarını çizer; sistem menüsünü kaldırınca gizlenirler
            long style = (long)GetWindowLongPtr(hwnd, -16);
            SetWindowLongPtr(hwnd, -16, (IntPtr)(style & ~0x00080000L));

            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, 4);                // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);             // köşeler yuvarlak
            int border = unchecked((int)0xFFFFFFFE);                   // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            int acrylic = 3;                                           // DWMSBT_TRANSIENTWINDOW
            return DwmSetWindowAttribute(hwnd, 38, ref acrylic, 4) == 0;
        }

        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
        public static void TrimMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }
    }
}
