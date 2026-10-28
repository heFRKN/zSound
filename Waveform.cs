using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace zSound
{
    /// Dalga formu + sürüklenebilir başlangıç/bitiş tutamaçları.
    /// Üstte zaman cetveli (tıkla → oradan dinle), altta mini harita (yakınlaşınca sürükle).
    /// Tekerlek: imlecin olduğu yere yakınlaş · Shift+tekerlek / orta tuş: kaydır
    public sealed class Waveform : FrameworkElement
    {
        static readonly Brush BarIn = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)));
        static readonly Brush BarOut = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00)));
        static readonly Brush Sel = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x99, 0x00, 0x33)));
        static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)));
        static readonly Brush MapSel = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0x99, 0x00, 0x33)));
        static readonly Brush MapView = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
        static readonly Brush TickText = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x84)));
        static readonly Pen Tick = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1));
        static readonly Pen Hover = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1));
        static readonly Pen MapEdge = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1));
        static readonly Pen Handle = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xC2, 0x1A, 0x55)), 2));
        static readonly Pen Head = Freeze(new Pen(Brushes.White, 1.5));
        static readonly Brush Grip = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0x1A, 0x55)));
        static readonly Typeface Font = new Typeface("Segoe UI");

        static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        const double RulerH = 18, MapH = 10, Gap = 6;
        /// en kısa seçim (saniye)
        public const double MinSel = 0.01;
        const double MinSpan = 0.05;

        float[] peaks;
        double dur, start, end, head = -1, hover = -1;
        double vs, ve;      // görünen aralık (saniye)
        int drag;           // 0 yok, 1 başlangıç, 2 bitiş, 3 kaydır, 4 mini harita
        double panX, panVs;

        /// kullanıcı sürükledi: (start, end) saniye
        public event Action<double, double> TrimChanged;
        /// tutamaç bırakıldı (true = başlangıç)
        public event Action<bool> TrimCommitted;
        /// cetvele tıklandı: o saniyeden dinle
        public event Action<double> Seek;
        /// yakınlaştırma/kaydırma değişti
        public event Action ViewChanged;

        public double Start => start;
        public double End => end;
        public double Duration => dur;
        public double Zoom => dur / Math.Max(1e-9, ve - vs);
        public bool HasData => peaks != null;

        public void Load(float[] p, double duration, double s, double e)
        {
            peaks = p;
            dur = Math.Max(0.01, duration);
            start = Math.Clamp(s, 0, dur);
            end = e <= 0 ? dur : Math.Clamp(e, 0, dur);
            if (end - start < MinSel) { start = 0; end = dur; }
            head = hover = -1;
            vs = 0; ve = dur;
            InvalidateVisual();
            ViewChanged?.Invoke();
        }

        public void SetTrim(double s, double e)
        {
            start = Math.Clamp(s, 0, dur);
            end = Math.Clamp(e <= 0 ? dur : e, Math.Min(dur, start + MinSel), dur);
            InvalidateVisual();
        }

        public double Playhead
        {
            set
            {
                if (Math.Abs(value - head) < 0.002) return;
                head = value;
                InvalidateVisual();
            }
        }

        // ───────────── görünüm ─────────────

        void SetView(double a, double span)
        {
            span = Math.Clamp(span, Math.Min(MinSpan, dur), dur);
            a = Math.Clamp(a, 0, dur - span);
            if (Math.Abs(a - vs) < 1e-9 && Math.Abs(a + span - ve) < 1e-9) return;
            vs = a; ve = a + span;
            InvalidateVisual();
            ViewChanged?.Invoke();
        }

        /// merkez sabit kalarak yakınlaş (f < 1) / uzaklaş (f > 1)
        public void ZoomBy(double f) => ZoomAt((vs + ve) / 2, 0.5, f);

        void ZoomAt(double t, double frac, double f)
        {
            double span = Math.Clamp((ve - vs) * f, Math.Min(MinSpan, dur), dur);
            SetView(t - frac * span, span);
        }

        public void ZoomFit() => SetView(0, dur);

        public void ZoomToSelection()
        {
            double pad = (end - start) * 0.08;
            SetView(start - pad, end - start + 2 * pad);
        }

        /// t görünmüyorsa görünüme getir
        public void Reveal(double t)
        {
            double span = ve - vs;
            if (t < vs || t > ve) SetView(t - span / 2, span);
        }

        // ───────────── çizim ─────────────

        double W => Math.Max(1, ActualWidth);
        double X(double sec) => (sec - vs) / (ve - vs) * W;
        double Sec(double x) => Math.Clamp(vs + x / W * (ve - vs), 0, dur);
        double WaveTop => RulerH;
        double WaveBottom => ActualHeight - MapH - Gap;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            double top = WaveTop, bot = WaveBottom, wh = bot - top;
            if (w < 2 || wh < 10) return;
            var waveRect = new Rect(0, top, w, wh);
            dc.DrawRoundedRectangle(Back, null, waveRect, 10, 10);
            if (peaks == null) return;

            DrawRuler(dc, w);

            double xs = X(start), xe = X(end);
            double cxs = Math.Clamp(xs, 0, w), cxe = Math.Clamp(xe, 0, w);

            dc.PushClip(new RectangleGeometry(waveRect, 10, 10));
            dc.DrawRectangle(Sel, null, new Rect(cxs, top, Math.Max(0, cxe - cxs), wh));

            // çubuklar: seçimin içi parlak, dışı soluk — iki geometri, iki çizim
            var gIn = new StreamGeometry();
            var gOut = new StreamGeometry();
            using (var cin = gIn.Open())
            using (var cout = gOut.Open())
            {
                const double step = 3, bw = 2;
                double mid = top + wh / 2, amp = wh / 2 - 6;
                double k = peaks.Length / dur;
                for (double x = 2; x < w - 2; x += step)
                {
                    int i0 = (int)(Sec(x) * k), i1 = (int)(Sec(x + step) * k);
                    if (i1 <= i0) i1 = i0 + 1;
                    float p = 0;
                    for (int i = i0; i < i1 && i < peaks.Length; i++) if (peaks[i] > p) p = peaks[i];
                    double bh = Math.Max(1.5, p * amp);
                    var c = x >= xs && x <= xe ? cin : cout;
                    c.BeginFigure(new Point(x, mid - bh), true, true);
                    c.LineTo(new Point(x + bw, mid - bh), false, false);
                    c.LineTo(new Point(x + bw, mid + bh), false, false);
                    c.LineTo(new Point(x, mid + bh), false, false);
                }
            }
            gIn.Freeze(); gOut.Freeze();
            dc.DrawGeometry(BarOut, null, gOut);
            dc.DrawGeometry(BarIn, null, gIn);

            dc.DrawRectangle(Shade, null, new Rect(0, top, cxs, wh));
            dc.DrawRectangle(Shade, null, new Rect(cxe, top, Math.Max(0, w - cxe), wh));

            if (hover >= 0 && drag == 0)
            {
                double x = X(hover);
                dc.DrawLine(Hover, new Point(x, top), new Point(x, bot));
            }

            foreach (var x in new[] { xs, xe })
            {
                if (x < -6 || x > w + 6) continue;
                dc.DrawLine(Handle, new Point(x, top), new Point(x, bot));
                dc.DrawRoundedRectangle(Grip, null, new Rect(x - 5, top + wh / 2 - 12, 10, 24), 4, 4);
            }

            if (head >= 0 && head >= vs && head <= ve)
            {
                double x = X(head);
                dc.DrawLine(Head, new Point(x, top + 4), new Point(x, bot - 4));
            }
            dc.Pop();

            if (head >= 0 && head >= vs && head <= ve)
            {
                double x = X(head);
                var tri = new StreamGeometry();
                using (var c = tri.Open())
                {
                    c.BeginFigure(new Point(x - 4, top - 6), true, true);
                    c.LineTo(new Point(x + 4, top - 6), false, false);
                    c.LineTo(new Point(x, top), false, false);
                }
                tri.Freeze();
                dc.DrawGeometry(Brushes.White, null, tri);
            }

            // mini harita: tüm dosya, seçim ve görünen pencere
            double my = h - MapH;
            dc.DrawRoundedRectangle(Back, null, new Rect(0, my, w, MapH), 4, 4);
            dc.DrawRectangle(MapSel, null, new Rect(start / dur * w, my + 3, Math.Max(1, (end - start) / dur * w), MapH - 6));
            var vr = new Rect(vs / dur * w, my + 0.5, Math.Max(6, (ve - vs) / dur * w), MapH - 1);
            dc.DrawRoundedRectangle(MapView, Zoom > 1.001 ? MapEdge : null, vr, 3, 3);
        }

        static readonly double[] Steps = { 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800 };

        void DrawRuler(DrawingContext dc, double w)
        {
            double pps = w / (ve - vs);
            double major = Steps[^1];
            foreach (var s in Steps) if (s * pps >= 70) { major = s; break; }
            int sub = major.ToString(CultureInfo.InvariantCulture).Contains('2') ? 4 : 5;
            double minor = major / sub;
            int dec = major >= 1 ? 0 : major >= 0.1 ? 1 : major >= 0.01 ? 2 : 3;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            long first = (long)Math.Floor(vs / minor);
            for (long i = first; i * minor <= ve + minor; i++)
            {
                double t = i * minor, x = X(t);
                if (x < 0 || x > w) continue;
                bool isMajor = i % sub == 0;
                dc.DrawLine(Tick, new Point(x, RulerH - (isMajor ? 7 : 3)), new Point(x, RulerH - 1));
                if (!isMajor) continue;
                var ft = new FormattedText(Label(t, dec), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 9.5, TickText, dpi);
                double tx = Math.Clamp(x + 3, 0, w - ft.Width);
                dc.DrawText(ft, new Point(tx, 0));
            }

            if (hover >= 0 && drag == 0)
            {
                // imlecin altındaki zaman (detaylı)
                var ft = new FormattedText(Label(hover, Math.Max(2, dec)), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 9.5, Brushes.White, dpi);
                double x = X(hover), tx = Math.Clamp(x - ft.Width / 2, 0, w - ft.Width);
                dc.DrawRoundedRectangle(Grip, null, new Rect(tx - 4, 0, ft.Width + 8, 14), 4, 4);
                dc.DrawText(ft, new Point(tx, 0.5));
            }
        }

        static string Label(double t, int dec)
        {
            t = Math.Max(0, t);
            int m = (int)(t / 60);
            double s = t - m * 60;
            string fmt = dec == 0 ? "00" : "00." + new string('0', dec);
            return m + ":" + Math.Round(s, dec).ToString(fmt, CultureInfo.InvariantCulture);
        }

        // ───────────── fare ─────────────

        int HitHandle(double x)
        {
            double ds = Math.Abs(x - X(start)), de = Math.Abs(x - X(end));
            if (Math.Min(ds, de) > 10) return 0;
            return ds <= de ? 1 : 2;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            if (peaks == null) return;
            var p = e.GetPosition(this);
            if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Right && p.Y < WaveBottom))
            {
                drag = 3; panX = p.X; panVs = vs;
                Cursor = Cursors.ScrollWE;
            }
            else if (e.ChangedButton != MouseButton.Left) return;
            else if (p.Y < WaveTop) { Seek?.Invoke(Sec(p.X)); e.Handled = true; return; }
            else if (p.Y > WaveBottom) { drag = 4; MoveMap(p.X); }
            else
            {
                drag = HitHandle(p.X);
                if (drag == 0) drag = Math.Abs(p.X - X(start)) < Math.Abs(p.X - X(end)) ? 1 : 2; // boşluğa tık → en yakın tutamaç oraya
                Move(p.X);
            }
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            switch (drag)
            {
                case 1: case 2: Move(p.X); return;
                case 3: SetView(panVs - (p.X - panX) / W * (ve - vs), ve - vs); return;
                case 4: MoveMap(p.X); return;
            }
            if (peaks == null) return;
            hover = p.Y >= 0 && p.Y <= WaveBottom ? Sec(p.X) : -1;
            Cursor = p.Y < WaveTop ? Cursors.Hand
                   : p.Y > WaveBottom ? (Zoom > 1.001 ? Cursors.SizeWE : Cursors.Arrow)
                   : HitHandle(p.X) != 0 ? Cursors.SizeWE : Cursors.Hand;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (hover < 0) return;
            hover = -1;
            InvalidateVisual();
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            if (drag == 0) return;
            int d = drag;
            drag = 0;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
            InvalidateVisual();
            if (d == 1 || d == 2) TrimCommitted?.Invoke(d == 1);
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (peaks == null) return;
            double notches = e.Delta / 120.0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                SetView(vs - notches * (ve - vs) * 0.15, ve - vs);
            else
            {
                double x = e.GetPosition(this).X;
                ZoomAt(Sec(x), Math.Clamp(x / W, 0, 1), Math.Pow(0.8, notches));
            }
            hover = Sec(e.GetPosition(this).X);
            e.Handled = true;
        }

        void MoveMap(double x)
        {
            double span = ve - vs;
            SetView(x / W * dur - span / 2, span);
        }

        void Move(double x)
        {
            double t = Sec(x);
            if (drag == 1) start = Math.Min(t, end - MinSel);
            else end = Math.Max(t, start + MinSel);
            start = Math.Max(0, start);
            end = Math.Min(dur, end);
            InvalidateVisual();
            TrimChanged?.Invoke(start, end);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            InvalidateVisual();
        }
    }
}
