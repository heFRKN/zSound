using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using zSound.Audio;
using zSound.Core;

namespace zSound
{
    public partial class MainWindow : Window
    {
        static readonly string[] Exts = { ".mp3", ".wav", ".ogg", ".oga", ".flac", ".m4a", ".aac", ".wma", ".opus", ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".3gp" };
        static readonly int[] Latencies = { 20, 30, 40, 60, 100 };

        readonly Config cfg;
        Settings S => cfg.Settings;
        readonly Engine engine = new Engine();
        readonly Hotkeys keys;
        readonly ObservableCollection<SoundVM> items = new ObservableCollection<SoundVM>();
        readonly ICollectionView view;
        readonly DispatcherTimer tick, saveTimer;
        readonly Dictionary<int, List<Sound>> held = new Dictionary<int, List<Sound>>();
        Tray tray;
        DeviceWatcher watcher;
        DispatcherTimer devTimer, apoTimer;
        SoundVM editing;
        Playback preview;       // düzenleyicideki dinleme
        int previewToken;
        bool previewStarting;   // dosya açılıyor, henüz çalmıyor
        Sound last;
        bool exiting, loadingSettings;

        public MainWindow(bool startInTray, Config config)
        {
            InitializeComponent();
            cfg = config;

            Width = Math.Max(MinWidth, S.WinW);
            Height = Math.Max(MinHeight, S.WinH);
            if (!double.IsNaN(S.WinX) && !double.IsNaN(S.WinY) && OnScreen(S.WinX, S.WinY))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = S.WinX; Top = S.WinY;
            }

            Library.Sweep(cfg.Sounds.Select(s => s.Path));
            foreach (var s in cfg.Sounds) items.Add(new SoundVM(s));
            view = CollectionViewSource.GetDefaultView(items);
            view.Filter = Filter;
            List.ItemsSource = view;
            FavFilter.IsChecked = S.OnlyFavorites;

            tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            tick.Tick += (_, _) => OnTick();
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); cfg.Save(); };

            engine.Changed += () => { if (!tick.IsEnabled) tick.Start(); UpdateStatus(); };
            engine.Apply(S, true);

            // kulaklık / mikrofon takılınca kendini yenile
            devTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            devTimer.Tick += (_, _) => { devTimer.Stop(); LoadDevices(); ApplySettings(true); UpdateApo(); };
            // eklentinin canlı durumu (mikrofonu kullanan uygulama var mı) — sadece bir int okur
            apoTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
            apoTimer.Tick += (_, _) => { UpdateApoText(); UpdateStatus(); };
            apoTimer.Start();
            try
            {
                watcher = new DeviceWatcher();
                watcher.Changed += () => Dispatcher.BeginInvoke(() => { devTimer.Stop(); devTimer.Start(); });
            }
            catch { }

            Wave.TrimChanged += (s, e) => { if (editing != null) { editing.S.StartSec = s; editing.S.EndSec = e >= Wave.Duration - 0.01 ? 0 : e; UpdateTrimText(); } };
            Wave.TrimCommitted += atStart =>
            {
                editing?.Refresh();
                SaveSoon();
                if (S.AuditionOnRelease) Audition(atStart);
            };
            Wave.Seek += t => StartPreview(t, t < Wave.End ? Wave.End : 0);
            Wave.ViewChanged += UpdateZoomText;

            keys = new Hotkeys(Dispatcher);
            keys.Key += OnKey;
            UpdateMouseHook();

            SyncBottom();
            InitSettingsView();
            UpdateApo();
            InitTray();
            UpdateEmpty();
            UpdateStatus();
            tick.Start();
            MeasureMissing();
            AdoptExisting();

            SourceInitialized += OnSourceInitialized;
            StateChanged += (_, _) =>
            {
                Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                if (WindowState == WindowState.Minimized) TrimSoon();
            };
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
            Drop += OnDrop;
            PreviewKeyDown += OnWindowKey;

            if (!(startInTray || S.StartMinimized)) Show();
            TrimSoon(4);
        }

        static bool OnScreen(double x, double y) =>
            x > SystemParameters.VirtualScreenLeft - 50 && y > SystemParameters.VirtualScreenTop - 50 &&
            x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

        void OnSourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            if (!Native.ApplyBackdrop(hwnd))
                Root.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x19)); // Win10: düz koyu
        }

        // ───────────── kütüphane ─────────────

        bool Filter(object o)
        {
            var vm = (SoundVM)o;
            if (S.OnlyFavorites && !vm.S.Favorite) return false;
            var q = Search.Text.Trim();
            return q.Length == 0 || vm.S.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                 || (vm.KeyText ?? "").Equals(q, StringComparison.OrdinalIgnoreCase);
        }

        void UpdateEmpty() => Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        async void AddPaths(IEnumerable<string> paths)
        {
            // klasörden gelenlerde zaten ekli olanları atla; tek tek eklenen dosya her seferinde
            // yeni ses olur (aynı sesin/videonun başka bir kısmını kullanmak için)
            var have = new HashSet<string>(cfg.Sounds.Select(s => s.Source ?? s.Path), StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                        files.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).Where(f => IsAudio(f) && !have.Contains(f)));
                    else if (File.Exists(p) && IsAudio(p)) files.Add(p);
                }
                catch { }
            }
            if (files.Count == 0) return;

            SoundVM firstNew = null;
            string err = null;
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                SetStatus(files.Count == 1 ? "Kopyalanıyor…" : $"Kopyalanıyor… ({i + 1}/{files.Count})", false);
                string stored;
                try { stored = await Task.Run(() => Library.Import(f)); }
                catch (Exception ex) { err = "Eklenemedi: " + Path.GetFileName(f) + " (" + ex.Message + ")"; continue; }
                var s = new Sound { Path = stored, Source = f, Name = UniqueName(Path.GetFileNameWithoutExtension(f)) };
                cfg.Sounds.Add(s);
                var vm = new SoundVM(s);
                items.Add(vm);
                firstNew ??= vm;
                Measure(vm);
            }
            if (err != null) SetStatus(err, true); else UpdateStatus();
            UpdateEmpty();
            SaveSoon();
            if (firstNew != null) List.ScrollIntoView(firstNew);
            // tek ses eklendiyse direkt kırpma ekranını aç
            if (firstNew != null && files.Count == 1) OpenEditor(firstNew);
        }

        /// aynı isim varsa "isim (2)", "isim (3)"…
        string UniqueName(string name)
        {
            var names = new HashSet<string>(cfg.Sounds.Select(s => s.Name), StringComparer.CurrentCultureIgnoreCase);
            if (!names.Contains(name)) return name;
            int n = 2;
            while (names.Contains($"{name} ({n})")) n++;
            return $"{name} ({n})";
        }

        /// eski sürümden kalan, dışarıdaki dosyayı gösteren sesleri kütüphaneye kopyala
        async void AdoptExisting()
        {
            foreach (var s in cfg.Sounds.Where(x => !Library.Owns(x.Path) && File.Exists(x.Path)).ToList())
            {
                try
                {
                    var src = s.Path;
                    var stored = await Task.Run(() => Library.Import(src));
                    if (s.Path != src) continue; // bu arada değişti
                    s.Source ??= src;
                    s.Path = stored;
                    SaveSoon();
                }
                catch { }
            }
        }

        static bool IsAudio(string p) => Exts.Contains(Path.GetExtension(p).ToLowerInvariant());

        void Measure(SoundVM vm, bool loudness = false)
        {
            var s = vm.S;
            bool loud = loudness || S.Normalize;
            Task.Run(() =>
            {
                try
                {
                    var (dur, gain) = Engine.Analyze(s.Path, loud);
                    Dispatcher.BeginInvoke(() =>
                    {
                        s.DurationSec = dur;
                        if (loud) { s.Gain = gain; s.Measured = true; }
                        vm.Refresh();
                        SaveSoon();
                    });
                }
                catch { }
            });
        }

        /// süresi bilinmeyenleri tek tek (sırayla, düşük CPU) ölç
        async void MeasureMissing()
        {
            foreach (var vm in items.Where(v => v.S.DurationSec <= 0 || (S.Normalize && !v.S.Measured)).ToList())
            {
                var s = vm.S;
                bool loud = S.Normalize && !s.Measured;
                try
                {
                    var (dur, gain) = await Task.Run(() => Engine.Analyze(s.Path, loud));
                    s.DurationSec = dur;
                    if (loud) { s.Gain = gain; s.Measured = true; }
                    vm.Refresh();
                }
                catch { }
            }
            SaveSoon();
        }

        void Remove(IEnumerable<SoundVM> vms)
        {
            var paths = new List<string>();
            foreach (var vm in vms.ToList())
            {
                engine.Stop(vm.S);
                cfg.Sounds.Remove(vm.S);
                items.Remove(vm);
                paths.Add(vm.S.Path);
            }
            // kopyayı başka ses kullanmıyorsa sil (çalan ses durana kadar biraz bekle)
            var unused = paths.Where(p => !cfg.Sounds.Any(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
            if (unused.Count > 0) Task.Delay(1000).ContinueWith(_ => unused.ForEach(Library.Delete));
            UpdateEmpty();
            SaveSoon();
            UpdateMouseHook();
        }

        IEnumerable<SoundVM> Selected => List.SelectedItems.Cast<SoundVM>();

        void Add_Click(object sender, RoutedEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
            m.Items.Add(Item("", "Dosya ekle…", AddFiles));
            m.Items.Add(Item("", "Klasör ekle…", AddFolder));
            m.IsOpen = true;
        }

        void AddFiles()
        {
            var d = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Ses / video seç",
                Filter = "Ses ve video|" + string.Join(";", Exts.Select(x => "*" + x)) + "|Tüm dosyalar|*.*"
            };
            if (d.ShowDialog(this) == true) AddPaths(d.FileNames);
        }

        void AddFolder()
        {
            var d = new OpenFolderDialog { Title = "Klasör seç (alt klasörler dahil)" };
            if (d.ShowDialog(this) == true) AddPaths(new[] { d.FolderName });
        }

        static MenuItem Item(string glyph, string text, Action a)
        {
            var mi = new MenuItem
            {
                Header = text,
                Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextDim"] }
            };
            mi.Click += (_, _) => a();
            return mi;
        }

        void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            RowMenu.Items.Clear();
            var sel = Selected.ToList();
            if (sel.Count == 0) { e.Handled = true; return; }
            var one = sel[0];
            if (sel.Count == 1)
            {
                bool playing = engine.IsPlaying(one.S);
                RowMenu.Items.Add(playing ? Item("", "Durdur", () => engine.Stop(one.S)) : Item("", "Çal", () => Play(one.S)));
                RowMenu.Items.Add(Item("", "Kırp / düzenle", () => OpenEditor(one)));
                RowMenu.Items.Add(Item("", "Kısayol ata", () => { OpenEditor(one); SoundKey_Click(BtnSoundKey, null); }));
                RowMenu.Items.Add(Item("", "Yukarı taşı", () => MoveBy(one, -1)));
                RowMenu.Items.Add(Item("", "Aşağı taşı", () => MoveBy(one, 1)));
            }
            bool allFav = sel.All(v => v.S.Favorite);
            RowMenu.Items.Add(Item(allFav ? "" : "", allFav ? "Favoriden çıkar" : "Favorilere ekle", () =>
            {
                foreach (var v in sel) { v.S.Favorite = !allFav; v.Refresh(); }
                view.Refresh(); SaveSoon();
            }));
            if (sel.Count == 1)
                RowMenu.Items.Add(Item("", "Dosya konumunu aç", () =>
                {
                    try { Process.Start("explorer.exe", "/select,\"" + (File.Exists(one.S.Source) ? one.S.Source : one.S.Path) + "\""); } catch { }
                }));
            RowMenu.Items.Add(new Separator());
            RowMenu.Items.Add(Item("", sel.Count > 1 ? $"{sel.Count} sesi kaldır" : "Kaldır", () => Remove(sel)));
        }

        void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((e.OriginalSource as DependencyObject) is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem li)
                Play(((SoundVM)li.DataContext).S);
        }

        void RowPlay_Click(object sender, RoutedEventArgs e)
        {
            var vm = (SoundVM)((FrameworkElement)sender).Tag;
            if (engine.IsPlaying(vm.S)) engine.Stop(vm.S); else Play(vm.S);
        }

        void List_KeyDown(object sender, KeyEventArgs e)
        {
            var vm = List.SelectedItem as SoundVM;
            if (vm != null && Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey == Key.Up || e.SystemKey == Key.Down))
            { MoveBy(vm, e.SystemKey == Key.Up ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Enter && vm != null) { Play(vm.S); e.Handled = true; }
            else if (e.Key == Key.Delete && vm != null) { Remove(Selected); e.Handled = true; }
            else if (e.Key == Key.F2 && vm != null) { OpenEditor(vm); e.Handled = true; }
        }

        void Search_TextChanged(object sender, TextChangedEventArgs e) => view.Refresh();

        void Search_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Search.Text = ""; List.Focus(); e.Handled = true; }
            else if (e.Key == Key.Down && List.Items.Count > 0) { List.SelectedIndex = 0; (List.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && List.Items.Count > 0) { Play(((SoundVM)List.Items[0]).S); e.Handled = true; }
        }

        void FavFilter_Click(object sender, RoutedEventArgs e)
        {
            S.OnlyFavorites = FavFilter.IsChecked == true;
            view.Refresh();
            SaveSoon();
        }

        // ── sürükleyerek sıralama ──
        Point dragStart;
        SoundVM dragItem;

        SoundVM RowAt(object src) =>
            src is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem li ? li.DataContext as SoundVM : null;

        void List_PreviewDown(object sender, MouseButtonEventArgs e)
        {
            dragStart = e.GetPosition(List);
            dragItem = RowAt(e.OriginalSource);
        }

        void List_PreviewMove(object sender, MouseEventArgs e)
        {
            if (dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(List) - dragStart;
            if (Math.Abs(d.Y) < 6 && Math.Abs(d.X) < 6) return;
            var it = dragItem;
            dragItem = null;
            DragDrop.DoDragDrop(List, new DataObject(typeof(SoundVM), it), DragDropEffects.Move);
            SaveSoon();
        }

        void List_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(SoundVM))) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            var src = (SoundVM)e.Data.GetData(typeof(SoundVM));
            var over = RowAt(e.OriginalSource);
            if (over == null || over == src) return;
            MoveTo(src, items.IndexOf(over)); // canlı: satır sürükledikçe yer değiştirir
        }

        void List_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(SoundVM))) e.Handled = true;
        }

        void MoveTo(SoundVM vm, int to)
        {
            int from = items.IndexOf(vm);
            if (from < 0 || to < 0 || to >= items.Count || from == to) return;
            items.Move(from, to);
            cfg.Sounds.Remove(vm.S);
            cfg.Sounds.Insert(to, vm.S);
            SaveSoon();
        }

        void MoveBy(SoundVM vm, int delta)
        {
            // filtre varsa görünen komşuyla yer değiştir
            var vis = view.Cast<SoundVM>().ToList();
            int i = vis.IndexOf(vm) + delta;
            if (i < 0 || i >= vis.Count) return;
            MoveTo(vm, items.IndexOf(vis[i]));
            List.SelectedItem = vm;
            List.ScrollIntoView(vm);
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(SoundVM))) return;
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
        }

        void OnWindowKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && EditView.Visibility == Visibility.Visible && keys.Capture == null
                && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBox)
            {
                PlaySel_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowView(LibraryView);
                Search.Focus(); Search.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && keys.Capture == null && LibraryView.Visibility != Visibility.Visible)
            {
                if (EditView.Visibility == Visibility.Visible) CloseEditor();
                ShowView(LibraryView);
                e.Handled = true;
            }
        }

        // ───────────── çalma ─────────────

        async void Play(Sound s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path)) { SetStatus("Dosya bulunamadı: " + s.Name, true); return; }
            last = s;
            await engine.Play(s);
            if (!tick.IsEnabled) tick.Start();
        }

        void OnTick()
        {
            bool busy = engine.Tick();
            var plays = engine.Plays;

            var on = new HashSet<Sound>();
            foreach (var p in plays) if (!p.Stopping && !p.Done) on.Add(p.Sound);
            foreach (var vm in items) vm.Playing = on.Contains(vm.S);

            Playback cur = null;
            for (int i = plays.Count - 1; i >= 0; i--) if (!plays[i].Stopping && !plays[i].Done) { cur = plays[i]; break; }

            if (editing != null)
            {
                if (preview != null && (preview.Stopping || preview.Done)) preview = null;
                var tryPb = plays.LastOrDefault(p => p.Sound == editing.S && !p.Preview && !p.Stopping && !p.Done);
                Wave.Playhead = (preview ?? tryPb)?.Position ?? -1;
                UpdateEditButtons(tryPb != null);
            }

            if (cur != null)
            {
                NowCard.Visibility = Visibility.Visible;
                NowName.Text = (on.Count > 1 ? $"({on.Count}) " : "") + cur.Sound.Name;
                double len = Math.Max(0.001, cur.End - cur.Start);
                double at = Math.Clamp(cur.Position - cur.Start, 0, len);
                NowTime.Text = SoundVM.Dur(at) + " / " + SoundVM.Dur(len);
                ProgFill.Width = ProgTrack.ActualWidth * (at / len);
                BtnPause.Content = cur.Paused ? "" : "";
            }
            else NowCard.Visibility = Visibility.Collapsed;

            if (!busy && plays.Count == 0) { tick.Stop(); UpdateStatus(); }
        }

        void OnKey(Hotkey k, bool down)
        {
            // kendi penceremizde yazı yazarken tetikleme
            if (IsActive && Keyboard.FocusedElement is TextBox) return;

            if (!down)
            {
                // basılı-tut sesleri: kombinasyonun herhangi bir tuşu bırakılınca sus
                if (held.Remove(k.Vk, out var hs))
                    foreach (var s in hs)
                    {
                        engine.Stop(s);
                        foreach (var l in held.Values) l.Remove(s);
                    }
                return;
            }

            // önce tam kombinasyon (Num1+Num2), yoksa tek tuş (Num2)
            if (k.IsChord && Fire(k)) return;
            Fire(k.IsChord ? k.Single() : k);
        }

        bool Fire(Hotkey k)
        {
            if (k.Same(S.KeyStopAll)) { engine.StopAll(); return true; }
            if (k.Same(S.KeyPauseAll)) { engine.TogglePause(); return true; }
            if (k.Same(S.KeyRandom)) { PlayRandom(); return true; }
            if (k.Same(S.KeyReplayLast)) { Play(last); return true; }
            if (k.Same(S.KeyToggleMic)) { S.SendToMic = !S.SendToMic; SyncBottom(); ApplySettings(false); return true; }
            if (k.Same(S.KeyToggleSpeakers)) { S.SendToSpeakers = !S.SendToSpeakers; SyncBottom(); ApplySettings(false); return true; }
            if (k.Same(S.KeyToggleMicPass)) { engine.SetMicMuted(!engine.MicMuted); return true; }

            bool any = false;
            foreach (var s in cfg.Sounds)
            {
                if (s.Key == null || !s.Key.Same(k)) continue;
                any = true;
                if (s.HoldToPlay)
                {
                    foreach (var vk in new[] { k.Vk, k.Vk2 })
                    {
                        if (vk == 0) continue;
                        if (!held.TryGetValue(vk, out var l)) held[vk] = l = new List<Sound>();
                        l.Add(s);
                    }
                    Play(s);
                }
                else if (S.PressAgainStops && engine.IsPlaying(s)) engine.Stop(s);
                else Play(s);
            }
            return any;
        }

        void PlayRandom()
        {
            var pool = view.Cast<SoundVM>().Select(v => v.S).ToList();
            if (pool.Count == 0) return;
            Sound pick;
            int guard = 0;
            do pick = pool[Random.Shared.Next(pool.Count)]; while (pool.Count > 1 && pick == last && guard++ < 5);
            Play(pick);
        }

        void Pause_Click(object sender, RoutedEventArgs e) => engine.TogglePause();
        void StopAll_Click(object sender, RoutedEventArgs e) => engine.StopAll();

        // ───────────── alt bar ─────────────

        void SyncBottom()
        {
            loadingSettings = true;
            TgMic.IsChecked = S.SendToMic;
            TgSpk.IsChecked = S.SendToSpeakers;
            SlMic.Value = S.MicVolume;
            SlSpk.Value = S.MonitorVolume;
            loadingSettings = false;
            UpdatePct();
        }

        void UpdatePct()
        {
            MicPct.Text = Math.Round(S.MicVolume * 100) + "%";
            SpkPct.Text = Math.Round(S.MonitorVolume * 100) + "%";
        }

        void TgMic_Click(object sender, RoutedEventArgs e) { S.SendToMic = TgMic.IsChecked == true; ApplySettings(false); }
        void TgSpk_Click(object sender, RoutedEventArgs e) { S.SendToSpeakers = TgSpk.IsChecked == true; ApplySettings(false); }
        void SlMic_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (loadingSettings || cfg == null) return; S.MicVolume = (float)e.NewValue; UpdatePct(); ApplySettings(false); }
        void SlSpk_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (loadingSettings || cfg == null) return; S.MonitorVolume = (float)e.NewValue; UpdatePct(); ApplySettings(false); }

        // ───────────── ayarlar ─────────────

        void ApplySettings(bool rebuild)
        {
            engine.Apply(S, rebuild);
            SaveSoon();
            UpdateStatus();
        }

        void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

        void InitSettingsView()
        {
            loadingSettings = true;
            LoadDevices();
            CboLatency.SelectedIndex = Math.Max(0, Array.IndexOf(Latencies, S.LatencyMs));
            SettingsPanel.DataContext = S;
            BuildGlobalKeys();
            UpdatePttText();
            loadingSettings = false;

            void changed(object o, RoutedEventArgs e)
            {
                if (loadingSettings) return;
                bool rebuild = (e.OriginalSource as FrameworkElement)?.Tag as string == "rebuild";
                if (e.OriginalSource is Button) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ApplySettings(rebuild));
            }
            SettingsPanel.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((o, e) => changed(o, e)));
            SettingsPanel.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(changed));
            SettingsPanel.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((o, e) => changed(o, e)));
        }

        void LoadDevices()
        {
            bool was = loadingSettings;
            loadingSettings = true;
            // liste bağlıyken değişirse WPF seçimi bulamaz ve ayara boş yazar (seçim "Varsayılan"a döner):
            // önce bağı kopar, listeyi yenile, kayıtlı seçimi geri koy
            var mon = S.MonitorDevice ?? ""; var mic = S.MicDevice ?? "";
            SettingsPanel.DataContext = null;
            CboMonitor.ItemsSource = Engine.Devices(DataFlow.Render, "Varsayılan hoparlör");
            CboMic.ItemsSource = Engine.Devices(DataFlow.Capture, "Varsayılan mikrofon");
            S.MonitorDevice = mon; S.MicDevice = mic;
            SettingsPanel.DataContext = S;
            // cihaz o an takılı değilse (ses hizmeti yeniden başlıyor vb.) seçim silinmesin, geri gelince yine seçilsin
            if (string.IsNullOrEmpty(S.MonitorDevice)) S.MonitorDevice = mon;
            if (string.IsNullOrEmpty(S.MicDevice)) S.MicDevice = mic;
            loadingSettings = was;
        }

        void RebindSettings()
        {
            bool was = loadingSettings;
            loadingSettings = true;
            SettingsPanel.DataContext = null;
            SettingsPanel.DataContext = S;
            loadingSettings = was;
        }

        void Latency_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CboLatency.SelectedIndex >= 0) S.LatencyMs = Latencies[CboLatency.SelectedIndex];
        }

        void BuildGlobalKeys()
        {
            var defs = new (string label, string prop)[]
            {
                ("Hepsini durdur", nameof(Settings.KeyStopAll)),
                ("Duraklat / devam", nameof(Settings.KeyPauseAll)),
                ("Rastgele ses", nameof(Settings.KeyRandom)),
                ("Son sesi tekrar çal", nameof(Settings.KeyReplayLast)),
                ("Mikrofona gönder aç/kapa", nameof(Settings.KeyToggleMic)),
                ("Hoparlör aç/kapa", nameof(Settings.KeyToggleSpeakers)),
                ("Kendi sesimi sustur / aç", nameof(Settings.KeyToggleMicPass)),
            };
            GlobalKeys.Children.Clear();
            for (int i = 0; i < defs.Length; i++)
            {
                var (label, prop) = defs[i];
                var pi = typeof(Settings).GetProperty(prop);
                if (i > 0) GlobalKeys.Children.Add(new Border { Style = (Style)FindResource("Div") });
                var row = new DockPanel { Style = (Style)FindResource("SetRow") };
                row.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
                var b = new Button { Style = (Style)FindResource("KeyBtn"), Content = KeyLabel((Hotkey)pi.GetValue(S)) };
                DockPanel.SetDock(b, Dock.Right);
                b.Click += (_, _) => Capture(b, h => pi.SetValue(S, h), () => KeyLabel((Hotkey)pi.GetValue(S)));
                b.MouseRightButtonUp += (_, _) => { pi.SetValue(S, null); b.Content = KeyLabel(null); UpdateMouseHook(); SaveSoon(); };
                row.Children.Add(b);
                GlobalKeys.Children.Add(row);
            }
        }

        static string KeyLabel(Hotkey h) => h == null || h.IsEmpty ? "Ata" : h.ToString();

        /// buton "Bir tuşa bas…" olur; Esc iptal, Backspace/Delete temizler
        void Capture(Button b, Action<Hotkey> set, Func<string> label)
        {
            b.Content = "Bir tuşa bas…";
            b.Foreground = (Brush)FindResource("Accent");
            keys.EnableMouse(true);
            keys.Capture = h =>
            {
                b.ClearValue(ForegroundProperty);
                if (h.Vk == 0x08 || h.Vk == 0x2E) set(null);
                else if (h.Vk != 0x1B)
                {
                    ClearDuplicates(h);
                    set(h);
                }
                b.Content = label();
                UpdateMouseHook();
                foreach (var vm in items) if (vm.HasKey || vm.S.Key != null) vm.Refresh();
                SaveSoon();
            };
        }

        void ClearDuplicates(Hotkey h)
        {
            foreach (var s in cfg.Sounds) if (s.Key != null && s.Key.Same(h)) s.Key = null;
            foreach (var p in typeof(Settings).GetProperties().Where(p => p.PropertyType == typeof(Hotkey)))
                if (p.GetValue(S) is Hotkey o && o.Same(h)) p.SetValue(S, null);
            BuildGlobalKeys();
        }

        void UpdateMouseHook()
        {
            static bool mouse(Hotkey h) => h != null && (h.Uses(0x04) || h.Uses(0x05) || h.Uses(0x06));
            bool any = cfg.Sounds.Any(s => mouse(s.Key)) ||
                       typeof(Settings).GetProperties().Where(p => p.PropertyType == typeof(Hotkey)).Any(p => mouse((Hotkey)p.GetValue(S)));
            keys.EnableMouse(any);
        }

        void UpdatePttText() => BtnPtt.Content = S.PushToTalkVk > 0 ? KeyNames.Name(S.PushToTalkVk) : "Kapalı";

        void Ptt_Click(object sender, RoutedEventArgs e)
        {
            Capture(BtnPtt, h => S.PushToTalkVk = h?.Vk ?? 0, () => S.PushToTalkVk > 0 ? KeyNames.Name(S.PushToTalkVk) : "Kapalı");
            // PTT tuşu kısayol sayılmaz, çakışma temizliği yapmayalım
        }

        void Ptt_Clear(object sender, MouseButtonEventArgs e) { S.PushToTalkVk = 0; UpdatePttText(); SaveSoon(); }

        void Normalize_Click(object sender, RoutedEventArgs e)
        {
            if (!S.Normalize) return;
            foreach (var vm in items.Where(v => !v.S.Measured)) Measure(vm, true);
        }

        void Autostart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (S.StartWithWindows) k.SetValue("zSound", "\"" + Environment.ProcessPath + "\" --tray");
                else k.DeleteValue("zSound", false);
            }
            catch { }
        }

        void OpenCfg_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(Config.Dir); Open(Config.Dir); }
        static void Open(string p) { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { } }

        void Refresh_Click(object sender, RoutedEventArgs e)
        {
            LoadDevices();
            ApplySettings(true);
            UpdateApo();
        }

        void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsView.Visibility == Visibility.Visible) { ShowView(LibraryView); return; }
            if (EditView.Visibility == Visibility.Visible) CloseEditor();
            MemText.Text = "RAM: " + (Process.GetCurrentProcess().WorkingSet64 / 1048576) + " MB";
            ShowView(SettingsView);
            UpdateApo();
        }

        void StatusPill_Click(object sender, MouseButtonEventArgs e)
        {
            if (EditView.Visibility == Visibility.Visible) CloseEditor();
            ShowView(SettingsView);
            UpdateApo();
        }

        void ShowView(UIElement v)
        {
            LibraryView.Visibility = v == LibraryView ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = v == SettingsView ? Visibility.Visible : Visibility.Collapsed;
            EditView.Visibility = v == EditView ? Visibility.Visible : Visibility.Collapsed;
            BtnSettings.Foreground = v == SettingsView ? (Brush)FindResource("Text") : (Brush)FindResource("TextDim");
        }

        void UpdateStatus()
        {
            if (!apoHere) SetStatus("Sadece hoparlör", false, "Mikrofon eklentisi kurulu değil — Ayarlar → Mikrofon eklentisi → Kur");
            else if (apoOld) SetStatus("Güncelle", true, "Mikrofon eklentisi eski — Ayarlar → Mikrofon eklentisi → Güncelle");
            else if (engine.LastError != null) SetStatus("Hata", true, engine.LastError);
            else if (engine.MicMuted) SetStatus("Sesin kapalı", true, "Kendi sesin susturuldu (kısayolla tekrar aç)");
            else if (engine.Plays.Count > 0) SetStatus("Çalıyor", false, micName);
            else SetStatus("Hazır", false, micName + (engine.MicReaders > 0 ? " · etkin" : " · şu an mikrofonu kullanan uygulama yok"));
        }

        // ───────────── mikrofon eklentisi ─────────────

        string micId, micName;
        bool apoHere, apoOld, apoBusy;

        /// seçili mikrofonu ve kurulum durumunu kayıt defterinden okur
        void UpdateApo()
        {
            (micId, micName) = Engine.ResolveMic(S.MicDevice);
            apoHere = micId != null && MicApo.IsInstalledOn(micId);
            apoOld = apoHere && MicApo.NeedsUpdate;
            BtnApoInstall.Content = apoOld ? "Güncelle" : apoHere ? "Yeniden kur" : "Kur";
            BtnApoRemove.Visibility = apoHere || MicApo.AnyInstalled ? Visibility.Visible : Visibility.Collapsed;
            BtnApoInstall.IsEnabled = BtnApoRemove.IsEnabled = !apoBusy && micId != null;
            UpdateApoText();
            UpdateStatus();
        }

        void UpdateApoText()
        {
            if (apoBusy || SettingsView.Visibility != Visibility.Visible) return;
            if (micId == null) ApoText.Text = "Mikrofon bulunamadı.";
            else if (!apoHere)
                ApoText.Text = (BtnApoRemove.Visibility == Visibility.Visible ? "Başka bir mikrofonda kurulu. " : "") +
                               "Kurulunca sesler doğrudan \"" + micName + "\" üzerinden gider. Yönetici izni ister.";
            else if (apoOld) ApoText.Text = "Kurulu eklenti eski sürüm, sesler mikrofona gitmez. \"Güncelle\"ye bas.";
            else if (engine.MicReaders > 0) ApoText.Text = "Etkin — sesler \"" + micName + "\" üzerinden gidiyor.";
            else ApoText.Text = "Kurulu. Mikrofonu bir uygulama (Discord, oyun, Windows ses ayarları…) açınca etkinleşir.";
        }

        void CboMic_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!loadingSettings) Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateApo);
        }

        void ApoInstall_Click(object sender, RoutedEventArgs e)
        {
            if (micId == null) return;
            if (MessageBox.Show(this, "zSound eklentisi \"" + micName + "\" mikrofonuna kurulacak.\n\n" +
                    "Yönetici izni istenecek ve Windows ses hizmeti yeniden başlatılacak: tüm sesler birkaç saniye kesilir, " +
                    "Discord mikrofonu kendiliğinden yeniden bağlanır.\n\n" +
                    "Kurulumdan sonra mikrofona 3 saniyelik bir test sesi gönderilip ölçülür; ses geçmezse her şey otomatik geri alınır.\n\nDevam edilsin mi?",
                    "zSound", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            RunApo("install", "Kuruluyor…");
        }

        void ApoRemove_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Eklenti tüm mikrofonlardan kaldırılacak ve değiştirilen her şey eski haline dönecek.\n\n" +
                    "Yönetici izni istenecek ve Windows ses hizmeti yeniden başlatılacak (sesler birkaç saniye kesilir).\n\nDevam edilsin mi?",
                    "zSound", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            RunApo("uninstall", "Kaldırılıyor…");
        }

        async void RunApo(string verb, string busyText)
        {
            apoBusy = true;
            BtnApoInstall.IsEnabled = BtnApoRemove.IsEnabled = false;
            ApoText.Text = busyText + " (ses hizmeti yeniden başlıyor)";
            var err = await MicApo.RunElevated(verb, micId);
            // ses hizmeti yeniden başladı: hoparlör çıkışını tazele
            await Task.Delay(1500);
            apoBusy = false;
            ApplySettings(true);
            UpdateApo();
            if (err != null) MessageBox.Show(this, err, "zSound", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        void SetStatus(string text, bool bad, string tip = null)
        {
            StatusText.Text = text;
            StatusDot.Fill = bad ? (Brush)FindResource("AccentHi") : new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x7A));
            StatusPill.ToolTip = tip;
        }

        // ───────────── düzenleyici ─────────────

        int waveToken;

        async void OpenEditor(SoundVM vm)
        {
            StopPreview();
            editing = vm;
            AuditionSwitch.IsChecked = S.AuditionOnRelease;
            var s = vm.S;
            EditPanel.DataContext = null;
            EditPanel.DataContext = s;
            EditPath.Text = s.Source ?? s.Path;
            BtnSoundKey.Content = KeyLabel(s.Key);
            ShowView(EditView);
            EditView.ScrollToTop();

            Wave.Load(null, 1, 0, 0);
            WaveLoading.Text = "Dalga formu yükleniyor…";
            WaveLoading.Visibility = Visibility.Visible;
            TrimStartText.Text = TrimEndText.Text = TrimLenText.Text = "";
            int token = ++waveToken;
            try
            {
                var (peaks, dur) = await Task.Run(() =>
                {
                    double d;
                    using (var st = Voice.Open(s.Path)) d = st.TotalTime.TotalSeconds;
                    // yakınlaşınca da detay kalsın: ~1 ms çözünürlük (uzun dosyalarda sınırlı)
                    var p = Engine.Peaks(s.Path, (int)Math.Clamp(d * 1000, 4000, 600000));
                    return (p, d);
                });
                if (token != waveToken || editing?.S != s) return; // bu arada başka ses açıldı
                s.DurationSec = dur;
                Wave.Load(peaks, dur, s.StartSec, s.EndSec);
                WaveLoading.Visibility = Visibility.Collapsed;
                UpdateTrimText();
            }
            catch (Exception ex)
            {
                if (token == waveToken) WaveLoading.Text = "Okunamadı: " + ex.Message;
            }
        }

        static string Ts(double sec) => TimeSpan.FromSeconds(Math.Round(Math.Max(0, sec), 2)).ToString(sec >= 3600 ? @"h\:mm\:ss\.ff" : @"m\:ss\.ff");

        void UpdateTrimText()
        {
            TrimStartText.Text = Ts(Wave.Start);
            TrimEndText.Text = Ts(Wave.End);
            TrimLenText.Text = Ts(Wave.End - Wave.Start);
        }

        void Nudge_Click(object sender, RoutedEventArgs e)
        {
            if (editing == null || Wave.Duration <= 0) return;
            var tag = (string)((FrameworkElement)sender).Tag;
            double d = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.01 : 0.1;
            if (tag[1] == '-') d = -d;
            if (tag[0] == 's') Wave.SetTrim(Math.Min(Wave.Start + d, Wave.End - Waveform.MinSel), Wave.End);
            else Wave.SetTrim(Wave.Start, Math.Max(Wave.End + d, Wave.Start + Waveform.MinSel));
            Wave.Reveal(tag[0] == 's' ? Wave.Start : Wave.End);
            editing.S.StartSec = Wave.Start;
            editing.S.EndSec = Wave.End >= Wave.Duration - 0.01 ? 0 : Wave.End;
            UpdateTrimText();
            SaveSoon();
        }

        void TrimReset_Click(object sender, RoutedEventArgs e)
        {
            if (editing == null) return;
            editing.S.StartSec = 0;
            editing.S.EndSec = 0;
            Wave.SetTrim(0, Wave.Duration);
            UpdateTrimText();
            SaveSoon();
        }

        void CloseEditor()
        {
            StopPreview();
            if (editing == null) return;
            if (editing.S.StartSec >= editing.S.EndSec && editing.S.EndSec > 0) editing.S.EndSec = 0;
            if (string.IsNullOrWhiteSpace(editing.S.Name)) editing.S.Name = Path.GetFileNameWithoutExtension(editing.S.Path);
            editing.Refresh();
            editing = null;
            view.Refresh();
            SaveSoon();
        }

        void EditBack_Click(object sender, RoutedEventArgs e) { CloseEditor(); ShowView(LibraryView); }
        void EditPreview_Click(object sender, RoutedEventArgs e)
        {
            if (editing == null) return;
            StopPreview();
            bool was = engine.Plays.Any(p => p.Sound == editing.S && !p.Preview && !p.Stopping && !p.Done);
            engine.Stop(editing.S);
            if (!was) Play(editing.S);
        }

        // ── dinleme (sadece hoparlör) ──

        const double AuditionSec = 1.5;

        async void StartPreview(double from, double to)
        {
            if (editing == null || !Wave.HasData) return;
            var s = editing.S;
            preview?.Stop();
            preview = null;
            if (!File.Exists(s.Path)) { SetStatus("Dosya bulunamadı: " + s.Name, true); return; }
            int token = ++previewToken;
            previewStarting = true;
            UpdateEditButtons(shownTrying);
            var pb = await engine.Preview(s, from, to);
            if (token == previewToken) previewStarting = false;
            if (token != previewToken || editing?.S != s) { pb?.Stop(); return; } // bu arada durduruldu / başka ses açıldı
            if (pb == null) { SetStatus(engine.LastError ?? "Çalınamadı", true); UpdateEditButtons(shownTrying); return; }
            preview = pb;
            if (!tick.IsEnabled) tick.Start();
        }

        void StopPreview()
        {
            previewToken++;
            previewStarting = false;
            preview?.Stop();
            preview = null;
            UpdateEditButtons(shownTrying);
        }

        /// kesilen yeri kısaca dinle: başlangıçtan sonrası ya da bitişten öncesi
        void Audition(bool atStart)
        {
            double len = Math.Min(AuditionSec, Wave.End - Wave.Start);
            if (atStart) StartPreview(Wave.Start, Wave.Start + len);
            else StartPreview(Wave.End - len, Wave.End);
        }

        void PlaySel_Click(object sender, RoutedEventArgs e)
        {
            if (shownPlaying) StopPreview();
            else StartPreview(Wave.Start, Wave.End);
        }

        void Audition_Click(object sender, RoutedEventArgs e) => Audition((string)((FrameworkElement)sender).Tag == "s");

        void AuditionSwitch_Click(object sender, RoutedEventArgs e)
        {
            S.AuditionOnRelease = AuditionSwitch.IsChecked == true;
            SaveSoon();
        }

        bool shownPlaying, shownTrying;

        void UpdateEditButtons(bool trying)
        {
            bool playing = previewStarting || preview != null;
            if (playing != shownPlaying)
            {
                shownPlaying = playing;
                PlaySelIcon.Text = playing ? "\uE71A" : "\uE768";
                PlaySelText.Text = playing ? "Durdur" : "Çal";
            }
            if (trying != shownTrying)
            {
                shownTrying = trying;
                TryIcon.Text = trying ? "\uE71A" : "\uE768";
                TryText.Text = trying ? "Durdur" : "Dene";
            }
        }

        void ZoomIn_Click(object sender, RoutedEventArgs e) => Wave.ZoomBy(0.5);
        void ZoomOut_Click(object sender, RoutedEventArgs e) => Wave.ZoomBy(2);
        void ZoomSel_Click(object sender, RoutedEventArgs e) => Wave.ZoomToSelection();
        void ZoomFit_Click(object sender, RoutedEventArgs e) => Wave.ZoomFit();

        void UpdateZoomText()
        {
            double z = Wave.Zoom;
            ZoomText.Text = z < 1.05 ? "1×" : z < 10 ? z.ToString("0.#") + "×" : Math.Round(z) + "×";
            BtnZoomOut.IsEnabled = BtnZoomFit.IsEnabled = z > 1.001;
        }

        void SoundKey_Click(object sender, RoutedEventArgs e)
        {
            var s = editing?.S;
            if (s == null) return;
            Capture(BtnSoundKey, h => s.Key = h, () => KeyLabel(s.Key));
        }

        void SoundKey_Clear(object sender, MouseButtonEventArgs e)
        {
            if (editing == null) return;
            editing.S.Key = null;
            BtnSoundKey.Content = KeyLabel(null);
            UpdateMouseHook();
            SaveSoon();
        }

        void EditReset_Click(object sender, RoutedEventArgs e)
        {
            if (editing == null) return;
            var s = editing.S;
            s.Volume = 1; s.Speed = 1; s.StartSec = 0; s.EndSec = 0; s.FadeInSec = 0; s.FadeOutSec = 0;
            s.Loop = false; s.HoldToPlay = false; s.Target = OutputTarget.Both;
            OpenEditor(editing);
        }

        void EditDelete_Click(object sender, RoutedEventArgs e)
        {
            if (editing == null) return;
            var vm = editing;
            StopPreview();
            editing = null;
            Remove(new[] { vm });
            ShowView(LibraryView);
        }

        // ───────────── pencere / tepsi ─────────────

        void InitTray()
        {
            tray = new Tray("zSound");
            var menu = new ContextMenu();
            menu.Items.Add(Item("", "Göster", ShowFromTray));
            menu.Items.Add(Item("", "Hepsini durdur", () => engine.StopAll()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("", "Çıkış", Exit));
            tray.Menu = menu;
            tray.Click += ShowFromTray;
            tray.Command += OnCommand;
        }

        void OnCommand(string cmd)
        {
            int i = cmd.IndexOf(':');
            var verb = i < 0 ? cmd : cmd[..i];
            var arg = i < 0 ? "" : cmd[(i + 1)..].Trim();
            switch (verb)
            {
                case "stop": engine.StopAll(); break;
                case "pause": engine.TogglePause(); break;
                case "random": PlayRandom(); break;
                case "add":
                    ShowFromTray();
                    AddPaths(new[] { arg });
                    break;
                case "play":
                    var s = cfg.Sounds.FirstOrDefault(x => x.Name.Equals(arg, StringComparison.OrdinalIgnoreCase))
                         ?? cfg.Sounds.FirstOrDefault(x => x.Name.StartsWith(arg, StringComparison.OrdinalIgnoreCase));
                    Play(s);
                    break;
            }
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void TrimSoon(double sec = 1.5)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
            t.Tick += (_, _) => { t.Stop(); Native.TrimMemory(); };
            t.Start();
        }

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Close_Click(object sender, RoutedEventArgs e)
        {
            if (S.MinimizeToTray) { Hide(); TrimSoon(); }
            else Exit();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!exiting && S.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                TrimSoon();
                return;
            }
            base.OnClosing(e);
        }

        public void Exit()
        {
            if (exiting) return;
            exiting = true;
            if (WindowState == WindowState.Normal && IsLoaded)
            {
                S.WinW = ActualWidth; S.WinH = ActualHeight; S.WinX = Left; S.WinY = Top;
            }
            saveTimer.Stop();
            keys.Dispose();
            watcher?.Dispose();
            apoTimer.Stop();
            engine.Dispose(); // eklenti mikrofona dokunmayı hemen bırakır
            cfg.Save();
            tray.Dispose();
            Close();
            Application.Current.Shutdown();
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (WindowState == WindowState.Normal && IsLoaded) { S.WinX = Left; S.WinY = Top; SaveSoon(); }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            if (WindowState == WindowState.Normal && IsLoaded) { S.WinW = ActualWidth; S.WinH = ActualHeight; SaveSoon(); }
        }
    }
}
