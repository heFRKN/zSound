using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace zSound.Core
{
    /// WinForms yüklemeden tepsi ikonu (Shell_NotifyIcon). Menü WPF ContextMenu — koyu tema.
    public sealed class Tray : IDisposable
    {
        const int WM_APP_TRAY = 0x8001, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
        const int NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID, uFlags, uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA d);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int idx, IntPtr[] large, IntPtr[] small, uint n);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);

        readonly HwndSource src;
        readonly int taskbarCreated;
        readonly IntPtr icon;
        NOTIFYICONDATA data;

        public event Action Click;
        /// başka bir zSound.exe'den gelen komut (--play isim, --stop …)
        public event Action<string> Command;

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref COPYDATASTRUCT l);

        /// çalışan örneğe komut yollar
        public static bool Send(string cmd)
        {
            var h = FindWindow(null, "zSoundTray");
            if (h == IntPtr.Zero) return false;
            var p = Marshal.StringToHGlobalUni(cmd);
            try
            {
                var cds = new COPYDATASTRUCT { dwData = (IntPtr)0x7A53, cbData = (cmd.Length + 1) * 2, lpData = p };
                SendMessage(h, 0x4A, IntPtr.Zero, ref cds);
            }
            finally { Marshal.FreeHGlobal(p); }
            return true;
        }
        public ContextMenu Menu;

        public Tray(string tip)
        {
            src = new HwndSource(new HwndSourceParameters("zSoundTray") { Width = 0, Height = 0, WindowStyle = 0 });
            src.AddHook(Wnd);
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            var small = new IntPtr[1];
            ExtractIconEx(Environment.ProcessPath, 0, null, small, 1);
            icon = small[0];
            data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = src.Handle,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_APP_TRAY,
                hIcon = icon,
                szTip = tip
            };
            Shell_NotifyIcon(NIM_ADD, ref data);
        }

        IntPtr Wnd(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_APP_TRAY)
            {
                int m = (int)l & 0xFFFF;
                if (m == WM_LBUTTONUP) Click?.Invoke();
                else if (m == WM_RBUTTONUP && Menu != null)
                {
                    SetForegroundWindow(src.Handle);
                    Menu.Placement = PlacementMode.MousePoint;
                    Menu.IsOpen = true;
                }
                handled = true;
            }
            else if (msg == 0x4A) // WM_COPYDATA
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(l);
                if (cds.dwData == (IntPtr)0x7A53 && cds.lpData != IntPtr.Zero)
                {
                    var cmd = Marshal.PtrToStringUni(cds.lpData);
                    src.Dispatcher.BeginInvoke(() => Command?.Invoke(cmd));
                }
                handled = true;
                return (IntPtr)1;
            }
            else if (msg == taskbarCreated) Shell_NotifyIcon(NIM_ADD, ref data); // explorer yeniden başladı
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Shell_NotifyIcon(NIM_DELETE, ref data);
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            src.Dispose();
        }
    }
}
