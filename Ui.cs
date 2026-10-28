using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using zSound.Core;

namespace zSound
{
    public sealed class PctConv : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) => Math.Round(System.Convert.ToDouble(v) * 100) + "%";
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
    }

    public sealed class SecConv : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) => System.Convert.ToDouble(v).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
    }

    /// enum ↔ ComboBox.SelectedIndex
    public sealed class EnumIdx : IValueConverter
    {
        public static readonly EnumIdx I = new EnumIdx();
        public object Convert(object v, Type t, object p, CultureInfo c) => v == null ? -1 : (int)v;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is int i && i >= 0 ? Enum.ToObject(t, i) : Binding.DoNothing;
    }

    public sealed class SoundVM : INotifyPropertyChanged
    {
        static readonly Brush Idle = (Brush)Application.Current.Resources["TextDim"];
        static readonly Brush Hot = (Brush)Application.Current.Resources["Accent"];

        public readonly Sound S;
        bool playing;

        public SoundVM(Sound s) { S = s; }

        public string Name => S.Name;
        public bool Fav => S.Favorite;
        public bool HasKey => S.Key != null && !S.Key.IsEmpty;
        public string KeyText => S.Key?.ToString();
        public string PlayGlyph => playing ? "" : "";
        public Brush PlayBrush => playing ? Hot : Idle;

        public string Sub
        {
            get
            {
                var s = S.DurationSec > 0 ? Dur(S.DurationSec) : "—";
                if (S.Target == OutputTarget.MicOnly) s += " · sadece mikrofon";
                else if (S.Target == OutputTarget.SpeakersOnly) s += " · sadece ben";
                if (Math.Abs(S.Speed - 1f) > 0.01f) s += " · " + S.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
                if (S.Loop) s += " · döngü";
                if (S.HoldToPlay) s += " · basılı tut";
                return s;
            }
        }

        public bool Playing
        {
            get => playing;
            set
            {
                if (playing == value) return;
                playing = value;
                Raise(nameof(PlayGlyph)); Raise(nameof(PlayBrush));
            }
        }

        public static string Dur(double sec)
        {
            if (sec < 0) sec = 0;
            var t = TimeSpan.FromSeconds(sec);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        public void Refresh() => Raise("");

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
