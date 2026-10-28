using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace zSound
{
    /// Dalga formu + sürüklenebilir başlangıç/bitiş tutamaçları
    public sealed class Waveform : FrameworkElement
    {
        static readonly Brush BarIn = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)));
        static readonly Brush BarOut = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00)));
        static readonly Brush Sel = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x99, 0x00, 0x33)));
        static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)));
        static readonly Pen Handle = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xC2, 0x1A, 0x55)), 2));
        static readonly Pen Head = Freeze(new Pen(Brushes.White, 1.5));
        static readonly Brush Grip = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0x1A, 0x55)));

        static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        float[] peaks;
        double dur, start, end, head = -1;
        int drag; // 0 yok, 1 başlangıç, 2 bitiş

        /// kullanıcı sürükledi: (start, end) saniye
        public event Action<double, double> TrimChanged;
        /// tutamaç bırakıldı
        public event Action TrimCommitted;

        public double Start => start;
        public double End => end;
        public double Duration => dur;

        public void Load(float[] p, double duration, double s, double e)
        {
            peaks = p;
            dur = Math.Max(0.01, duration);
            start = Math.Clamp(s, 0, dur);
            end = e <= 0 ? dur : Math.Clamp(e, 0, dur);
            if (end - start < 0.05) { start = 0; end = dur; }
            head = -1;
            InvalidateVisual();
        }

        public void SetTrim(double s, double e)
        {
            start = Math.Clamp(s, 0, dur);
            end = Math.Clamp(e <= 0 ? dur : e, start + 0.05, dur);
            InvalidateVisual();
        }

        public double Playhead
        {
            set
            {
                if (Math.Abs(value - head) < 0.005) return;
                head = value;
                InvalidateVisual();
            }
        }

        double X(double sec) => sec / dur * ActualWidth;
        double Sec(double x) => Math.Clamp(x / Math.Max(1, ActualWidth) * dur, 0, dur);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, w, h), 10, 10);
            if (peaks == null || w < 2) return;

            double xs = X(start), xe = X(end);
            dc.DrawRectangle(Sel, null, new Rect(xs, 0, Math.Max(0, xe - xs), h));

            // çubuklar: seçimin içi parlak, dışı soluk — iki geometri, iki çizim
            var gIn = new StreamGeometry();
            var gOut = new StreamGeometry();
            using (var cin = gIn.Open())
            using (var cout = gOut.Open())
            {
                const double step = 3, bw = 2;
                double mid = h / 2, amp = h / 2 - 8;
                for (double x = 2; x < w - 2; x += step)
                {
                    int i0 = (int)(x / w * peaks.Length), i1 = (int)((x + step) / w * peaks.Length);
                    float p = 0;
                    for (int i = i0; i <= i1 && i < peaks.Length; i++) if (peaks[i] > p) p = peaks[i];
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

            dc.DrawRectangle(Shade, null, new Rect(0, 0, xs, h));
            dc.DrawRectangle(Shade, null, new Rect(xe, 0, Math.Max(0, w - xe), h));

            foreach (var x in new[] { xs, xe })
            {
                dc.DrawLine(Handle, new Point(x, 0), new Point(x, h));
                dc.DrawRoundedRectangle(Grip, null, new Rect(x - 5, h / 2 - 12, 10, 24), 4, 4);
            }

            if (head >= start && head <= end)
            {
                double x = X(head);
                dc.DrawLine(Head, new Point(x, 4), new Point(x, h - 4));
            }
        }

        int HitHandle(double x)
        {
            double ds = Math.Abs(x - X(start)), de = Math.Abs(x - X(end));
            if (Math.Min(ds, de) > 10) return 0;
            return ds <= de ? 1 : 2;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (peaks == null) return;
            double x = e.GetPosition(this).X;
            drag = HitHandle(x);
            if (drag == 0) drag = Math.Abs(x - X(start)) < Math.Abs(x - X(end)) ? 1 : 2; // boşluğa tık → en yakın tutamaç oraya
            Move(x);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            double x = e.GetPosition(this).X;
            if (drag != 0) Move(x);
            else Cursor = HitHandle(x) != 0 ? Cursors.SizeWE : Cursors.Hand;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (drag == 0) return;
            drag = 0;
            ReleaseMouseCapture();
            TrimCommitted?.Invoke();
        }

        void Move(double x)
        {
            double t = Sec(x);
            if (drag == 1) start = Math.Min(t, end - 0.05);
            else end = Math.Max(t, start + 0.05);
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
