using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace GlassPlayer;

// The "deck": record motion, album accent colour, the staggered reveal and the frosted grain.
public partial class MainWindow
{
    static readonly Color[] DefaultAccent = { Color.FromRgb(0x7C, 0xF2, 0xA8), Color.FromRgb(0x5A, 0x8C, 0xFF) };
    readonly Storyboard _spin = new();
    static double Slow = 1;   // >1 plays the motion in slow motion (used when recording the showcase GIF)
    bool? _deckPlaying;

    // ---------- record: spins (and the NOW PLAYING dot breathes) only while music plays ----------

    void BuildSpin()
    {
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(6)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(turn, Record);
        Storyboard.SetTargetProperty(turn, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(RotateTransform.Angle)"));
        _spin.Children.Add(turn);

        var breathe = new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(breathe, NowDot);
        Storyboard.SetTargetProperty(breathe, new PropertyPath(OpacityProperty));
        _spin.Children.Add(breathe);

        Timeline.SetDesiredFrameRate(_spin, 15);
        _spin.Begin(this, true);
        _spin.Pause(this);
        IsVisibleChanged += (_, _) => { if (IsVisible && _deckPlaying == true && _settings.Spin) _spin.Resume(this); else _spin.Pause(this); };
    }

    public bool SpinEnabled => _settings.Spin;

    public void ToggleSpin()
    {
        _settings.Spin = !_settings.Spin;
        _settings.Save();
        if (_settings.Spin && _deckPlaying == true && IsVisible) _spin.Resume(this); else _spin.Pause(this);
    }

    void SetDeck(bool playing)
    {
        if (_deckPlaying == playing) return;
        _deckPlaying = playing;

        // Playing: the record slides out of the sleeve with a little overshoot. Paused: it tucks back in.
        var slide = new DoubleAnimation(playing ? 28 : 0, TimeSpan.FromMilliseconds((playing ? 760 : 480) * Slow))
        {
            EasingFunction = playing ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
                                     : new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        RecordSlide.BeginAnimation(TranslateTransform.XProperty, slide);

        NowText.Text = playing ? "N O W   P L A Y I N G" : "P A U S E D";
        if (playing && IsVisible && _settings.Spin) _spin.Resume(this); else _spin.Pause(this);
        if (!playing) NowDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, TimeSpan.FromMilliseconds(300)));
        else NowDot.BeginAnimation(OpacityProperty, null);
    }

    // ---------- one orchestrated entrance, instead of everything popping in at once ----------

    void Reveal()
    {
        Record.Opacity = 1;
        Stage(ArtHost, 40, 0);
        Stage(ArtCrisp, 40, 0);
        Hold(ArtScale, ScaleTransform.ScaleXProperty, 0.86, 1, 40, 620);   // sleeve pops (shared by both cover layers)
        Hold(ArtScale, ScaleTransform.ScaleYProperty, 0.86, 1, 40, 620);
        Stage(Record, 220, 0);
        Stage(NowRow, 160, 8);
        Stage(TitleRow, 220, 10);
        Stage(ArtistRow, 290, 10);
        Stage(ProgressRow, 340, 0);
        Hold(TrackReveal, ScaleTransform.ScaleXProperty, 0, 1, 360, 620);
        Stage(ShuffleBtn, 420, 8);
        Stage(PrevBtn, 460, 8);
        Stage(PlayBtn, 500, 10);
        Stage(NextBtn, 540, 8);
        Stage(PinBtn, 580, 8);
    }

    // Fade in (and rise) after a delay. Key frames hold the hidden state until the delay ends,
    // then the animation lets go so the element keeps its own opacity (e.g. a dimmed toggle).
    static void Stage(UIElement el, int delayMs, double rise, int durMs = 460)
    {
        double target = el.Opacity > 0 ? el.Opacity : 1;
        el.BeginAnimation(UIElement.OpacityProperty, Frames(0, target, delayMs, durMs, new CubicEase { EasingMode = EasingMode.EaseOut }));
        if (rise == 0) return;
        if (el.RenderTransform is not TranslateTransform t || t.IsFrozen) { t = new TranslateTransform(); el.RenderTransform = t; }
        t.BeginAnimation(TranslateTransform.YProperty, Frames(rise, 0, delayMs, durMs + 120, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }));
    }

    static void Hold(Animatable target, DependencyProperty dp, double from, double to, int delayMs, int durMs) =>
        target.BeginAnimation(dp, Frames(from, to, delayMs, durMs, new QuinticEase { EasingMode = EasingMode.EaseOut }));

    static DoubleAnimationUsingKeyFrames Frames(double from, double to, int delayMs, int durMs, IEasingFunction ease)
    {
        var k = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        k.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        k.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs * Slow))));
        k.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds((delayMs + durMs) * Slow)), ease));
        return k;
    }

    // ---------- album accent: one dominant colour per cover ----------

    void SetAccent(Color[] cols)
    {
        Color a = cols[0], b = cols.Length > 1 ? cols[1] : cols[0];
        var dur = TimeSpan.FromMilliseconds(900);
        Tint((SolidColorBrush)FindResource("Accent"), a, dur);
        double lum = (0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255;
        Tint((SolidColorBrush)FindResource("OnAccent"), lum > 0.6 ? Color.FromRgb(13, 15, 20) : Colors.White, dur);
        AuraA0.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(WithA(a, 0x6B), dur));
        AuraA1.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(WithA(a, 0x1F), dur));
        AuraA2.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(WithA(a, 0x00), dur));
        AuraB0.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(WithA(b, 0x40), dur));
        AuraB1.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(WithA(b, 0x00), dur));
    }

    static void Tint(SolidColorBrush br, Color c, TimeSpan d) => br.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, d));

    // Most vivid pixel = accent; the opposite corner's average = the second, quieter glow.
    static Color[] Palette(BitmapSource src)
    {
        try
        {
            BitmapSource b = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            b = new TransformedBitmap(b, new ScaleTransform(16.0 / b.PixelWidth, 16.0 / b.PixelHeight));
            int w = b.PixelWidth, h = b.PixelHeight;
            var px = new byte[w * h * 4];
            b.CopyPixels(px, w * 4, 0);
            Color At(int x, int y) { int i = (y * w + x) * 4; return Color.FromRgb(px[i + 2], px[i + 1], px[i]); }

            Color vivid = Colors.White; double best = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                var (_, s, v) = ToHsv(At(x, y));
                double score = s * s * v * (v > 0.2 ? 1 : 0.1);
                if (score > best) { best = score; vivid = At(x, y); }
            }
            long r = 0, g = 0, bl = 0, n = 0;
            for (int y = h / 2; y < h; y++) for (int x = w / 2; x < w; x++) { var c = At(x, y); r += c.R; g += c.G; bl += c.B; n++; }
            var corner = Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(bl / n));
            return new[] { Boost(vivid, true), Boost(corner, false) };
        }
        catch { return DefaultAccent; }
    }

    static Color Boost(Color c, bool accent)
    {
        var (hue, s, v) = ToHsv(c);
        if (s < 0.12) return accent ? Color.FromRgb(0xF2, 0xEE, 0xE6) : Color.FromRgb(0x8A, 0x8F, 0xA8);   // grey covers: warm paper white
        s = Math.Clamp(s * 1.25 + 0.05, 0.45, 0.9);
        v = accent ? Math.Clamp(v * 1.1 + 0.15, 0.68, 0.98) : Math.Clamp(v, 0.45, 0.85);
        return FromHsv(hue, s, v);
    }

    static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x) : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    static Color WithA(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static ImageBrush MakeGrain()
    {
        const int N = 128;
        var px = new byte[N * N * 4];
        var rnd = new Random(11);
        for (int i = 0; i < px.Length; i += 4) { byte v = (byte)rnd.Next(256); px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(N, N, 96, 96, PixelFormats.Bgra32, null, px, N * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp) { TileMode = TileMode.Tile, Stretch = Stretch.None, Viewport = new Rect(0, 0, N, N), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
