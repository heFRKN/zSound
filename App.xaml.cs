using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace zSound
{
    public partial class App : Application
    {
        Mutex mutex;
        EventWaitHandle showSignal;

        protected override void OnStartup(StartupEventArgs e)
        {
            // mikrofon eklentisi kur/kaldır: yönetici olarak açılan arayüzsüz kopya (zSound zaten açıkken de çalışmalı)
            if (e.Args.Length > 0 && e.Args[0] == "--apo")
            {
                Shutdown(Core.MicApo.RunCommand(e.Args));
                return;
            }

            mutex = new Mutex(true, "zSound.single", out bool first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "zSound.show");
            if (!first)
            {
                // zSound.exe --play "isim" | --stop | --pause | --random  → çalışan örneğe ilet
                var cmd = e.Args.Length > 0 ? e.Args[0].TrimStart('-').ToLowerInvariant() : null;
                if (cmd is "play" or "stop" or "pause" or "random" or "add")
                    Core.Tray.Send(cmd + (e.Args.Length > 1 ? ":" + string.Join(" ", e.Args.Skip(1)) : ""));
                else showSignal.Set(); // açık olanı öne getir
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, ex) =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Core.Config.Dir);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(Core.Config.Dir, "error.log"), DateTime.Now + "  " + ex.Exception + "\r\n\r\n");
                }
                catch { }
                ex.Handled = true;
            };

            // GPU sürücüsü yüklenmez → VRAM ~0, RAM daha düşük. Arayüz statik, CPU farkı yok.
            var cfg = Core.Config.Load();
            if (!cfg.Settings.GpuRender)
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            base.OnStartup(e);
            bool tray = e.Args.Contains("--tray");
            var w = new MainWindow(tray, cfg);
            MainWindow = w;
            SessionEnding += (_, _) => w.Exit();

            var t = new Thread(() =>
            {
                while (showSignal.WaitOne())
                    Dispatcher.BeginInvoke(() => w.ShowFromTray());
            }) { IsBackground = true };
            t.Start();
        }
    }
}
