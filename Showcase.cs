using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace GlassPlayer;

// Showcase mode (env GLASSPLAYER_SHOWCASE=<folder>): renders marketing images of the real UI over generated
// wallpapers, with made-up tracks and generated cover art (no real album art, nothing from the desktop),
// plus the frames of the entrance animation. tools/make-showcase.ps1 turns the frames into a GIF.
public partial class MainWindow
{
    bool _showcase;
    const double WallW = 760, WallH = 440;

    sealed record Scene(string Name, string Wall, string Cover, string Title, string Artist,
                        bool Playing, bool Shuffle, double Progress, int Seconds);

    static readonly Scene[] Scenes =
    {
        new("hero",   "ocean",  "tides", "Glass Tides", "Nova Ardent", true,  false, 0.38, 214),
        new("dusk",   "dusk",   "suns",  "Paper Suns",  "Ilse Marrow", false, true,  0.62, 187),
        new("forest", "forest", "orbit", "Slow Orbit",  "Kairo Lune",  true,  true,  0.21, 248),
    };

    public async void RunShowcase(string outDir)
    {
        _showcase = true;
        Directory.CreateDirectory(outDir);
        Width = DesignW + 2 * Pad; Height = DesignH + 2 * Pad;
        Left = -8000; Top = -8000;          // rendered off-screen; we only ever take pictures of it
        Topmost = true; SetPinVisual();
        Show();
        Root.BeginAnimation(OpacityProperty, null); Root.Opacity = 1;
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootScale.ScaleX = RootScale.ScaleY = 1;
        Reveal();
        await Task.Delay(1500);

        try
        {
            foreach (var s in Scenes)
            {
                await StageScene(s);
                Save(Composite(2.0, null), IOPath.Combine(outDir, $"{s.Name}.png"));
            }
            await RecordEntrance(Scenes[0], IOPath.Combine(outDir, "frames"));
        }
        catch (Exception ex) { File.WriteAllText(IOPath.Combine(outDir, "error.txt"), ex.ToString()); }
        Application.Current.Shutdown();
    }

    Canvas? _wall;
    double WinX => Math.Round((WallW - ActualWidth) / 2);
    double WinY => Math.Round((WallH - ActualHeight) / 2);

    async Task StageScene(Scene s)
    {
        _wall = MakeWallpaper(s.Wall);
        _wallCache = null;
        TitleText.Text = s.Title;
        ArtistText.Text = s.Artist;
        SetArt(Render(MakeCover(s.Cover), 300, 300, 1));
        _end = TimeSpan.FromSeconds(s.Seconds);
        _pos = TimeSpan.FromSeconds(s.Seconds * s.Progress);
        _playing = false;
        SetPlayIcon(s.Playing);
        SetShuffle(s.Shuffle, true);
        SetDeck(s.Playing);

        // The glass: the same blur pipeline as live, fed with the wallpaper under the card instead of the screen.
        double cx = WinX + Pad - Inflate, cy = WinY + Pad - Inflate;
        double cw = ActualWidth - 2 * Pad + 2 * Inflate, ch = ActualHeight - 2 * Pad + 2 * Inflate;
        var wall1x = Render(_wall, (int)WallW, (int)WallH, 1);
        var crop = new CroppedBitmap(wall1x, new Int32Rect((int)cx, (int)cy, (int)cw, (int)ch));
        var small = new FormatConvertedBitmap(new TransformedBitmap(crop, new ScaleTransform(1.0 / Downscale, 1.0 / Downscale)), PixelFormats.Bgra32, null, 0);
        int sw = small.PixelWidth, sh = small.PixelHeight;
        var px = new byte[sw * sh * 4];
        small.CopyPixels(px, sw * 4, 0);
        ApplyBackdrop(px, sw, sh);

        UpdateProgress();
        await Task.Delay(1300);   // let the colour, cover and record animations settle
    }

    // Window rendered at `scale`, placed on the wallpaper. `region` crops around the widget (for the GIF).
    BitmapSource Composite(double scale, Rect? region)
    {
        var win = new RenderTargetBitmap((int)(ActualWidth * scale), (int)(ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        win.Render(this);
        var r = region ?? new Rect(0, 0, WallW, WallH);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-r.X, -r.Y));
            dc.DrawImage(_wallCache ??= Render(_wall!, (int)WallW, (int)WallH, scale), new Rect(0, 0, WallW, WallH));
            dc.DrawImage(win, new Rect(WinX, WinY, ActualWidth, ActualHeight));
            dc.Pop();
        }
        var outBmp = new RenderTargetBitmap((int)(r.Width * scale), (int)(r.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        outBmp.Render(dv);
        outBmp.Freeze();
        return outBmp;
    }
    BitmapSource? _wallCache;

    // Entrance (staggered reveal), then the record slides out and spins: saved as timed frames.
    async Task RecordEntrance(Scene s, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        await StageScene(s);
        _wallCache = null;
        SetDeck(false);
        SetPlayIcon(false);
        Root.Opacity = 0;
        await Task.Delay(700);

        // Each frame is a full high-res render (~100 ms), so play everything 5x slower and speed it back up in the GIF.
        const double scale = 1.5;
        Slow = 5;
        _spin.SetSpeedRatio(this, 1 / Slow);
        var region = new Rect(WinX - 48, WinY - 40, ActualWidth + 96, ActualHeight + 80);
        var times = new List<long>();
        var clock = Stopwatch.StartNew();

        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380 * Slow)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var pop = new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(560 * Slow)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        Reveal();
        bool slid = false;
        int i = 0;
        while (clock.ElapsedMilliseconds < 3600 * Slow)
        {
            if (!slid && clock.ElapsedMilliseconds > 1150 * Slow) { SetPlayIcon(true); SetDeck(true); slid = true; }   // "press play"
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            times.Add(clock.ElapsedMilliseconds);
            Save(Composite(scale, region), IOPath.Combine(dir, $"f{i++:000}.png"));
            await Task.Delay(10);
        }

        // ffmpeg concat list with each frame's real duration, so the animation keeps its true timing.
        var lines = new List<string>();
        for (int k = 0; k < times.Count; k++)
        {
            double dur = ((k + 1 < times.Count ? times[k + 1] : times[k] + 1500 * Slow) - times[k]) / 1000.0 / Slow;   // hold the last frame before the loop
            lines.Add($"file 'f{k:000}.png'");
            lines.Add("duration " + dur.ToString("0.000", CultureInfo.InvariantCulture));
        }
        lines.Add($"file 'f{times.Count - 1:000}.png'");
        File.WriteAllLines(IOPath.Combine(dir, "frames.txt"), lines);
        Slow = 1;
    }

    // ---------- generated art (all original, nothing licensed) ----------

    static Canvas MakeWallpaper(string kind)
    {
        var (bg, blobs, ribbons) = kind switch
        {
            "dusk" => (new[] { "#FF2A1846", "#FFB8417C", "#FFFF9C5B" }, new[] { "#FFFFD27A", "#FF7A3CF0", "#FFFF5E7E" }, new[] { "#FFFFC6A6", "#FFFF7AA8" }),
            "forest" => (new[] { "#FF0C2620", "#FF1D6A4A", "#FFB7E07A" }, new[] { "#FFE8F7B0", "#FF0F4F3A", "#FF4FD08A" }, new[] { "#FFD9F2A6", "#FF7FE0B0" }),
            _ => (new[] { "#FFCFE4F7", "#FF3D7BEA", "#FF0A1F7A" }, new[] { "#FFFFF3D6", "#FF5FA8FF", "#FF0B3BD1" }, new[] { "#FFFFFFFF", "#FF8FC0FF" }),
        };
        var c = new Canvas { Width = WallW, Height = WallH, ClipToBounds = true };
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.6, 1) };
        for (int i = 0; i < bg.Length; i++) g.GradientStops.Add(new GradientStop(Hex(bg[i]), i / (double)(bg.Length - 1)));
        c.Children.Add(new Rectangle { Width = WallW, Height = WallH, Fill = g });

        var spots = new[] { (-120.0, -160.0, 520.0), (420.0, 60.0, 480.0), (120.0, 260.0, 440.0) };
        for (int i = 0; i < blobs.Length; i++)
        {
            var (x, y, d) = spots[i];
            var col = Hex(blobs[i]);
            var e = new Ellipse
            {
                Width = d, Height = d, Opacity = 0.85,
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(col, 0), new GradientStop(Color.FromArgb(110, col.R, col.G, col.B), 0.5), new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1),
                }),
            };
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            c.Children.Add(e);
        }

        // flowing glass ribbons, like folded light
        var paths = new[]
        {
            "M -80,330 C 180,120 360,520 840,180",
            "M -80,120 C 220,300 480,-40 840,300",
        };
        for (int i = 0; i < paths.Length; i++)
        {
            var col = Hex(ribbons[i]);
            var p = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(paths[i]), StrokeThickness = i == 0 ? 120 : 70, Opacity = i == 0 ? 0.32 : 0.22,
                Stroke = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), GradientStops = { new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 0), new GradientStop(col, 0.5), new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1) } },
                Effect = new BlurEffect { Radius = 28, RenderingBias = RenderingBias.Quality },
            };
            c.Children.Add(p);
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(paths[i]), Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), StrokeThickness = 1.2, Effect = new BlurEffect { Radius = 1.5 } });
        }
        return c;
    }

    static Canvas MakeCover(string kind)
    {
        var c = new Canvas { Width = 300, Height = 300, ClipToBounds = true };
        void Add(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); c.Children.Add(e); }
        Brush Lin(string a, string b, double ex = 1, double ey = 1) => new LinearGradientBrush(Hex(a), Hex(b), new Point(0, 0), new Point(ex, ey));

        switch (kind)
        {
            case "suns":
                Add(new Rectangle { Width = 300, Height = 300, Fill = Lin("#FFFF6A3D", "#FFFFC15A", 0, 1) }, 0, 0);
                Add(new Ellipse { Width = 170, Height = 170, Fill = Lin("#FFFFF4D6", "#FFFFD089", 0, 1) }, 65, 52);
                Add(new Ellipse { Width = 90, Height = 90, Fill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) }, 190, 30);
                Add(new Rectangle { Width = 300, Height = 110, Fill = Lin("#FF4A1D52", "#FF2A1036", 0, 1) }, 0, 190);
                for (int i = 0; i < 5; i++) Add(new Rectangle { Width = 300, Height = 3, Fill = new SolidColorBrush(Color.FromArgb(60, 255, 200, 150)) }, 0, 200 + i * 18);
                break;
            case "orbit":
                Add(new Rectangle { Width = 300, Height = 300, Fill = Lin("#FF0D3A2C", "#FF8EDB5A") }, 0, 0);
                for (int i = 0; i < 4; i++)
                {
                    double d = 120 + i * 50;
                    Add(new Ellipse { Width = d, Height = d * 0.42, Stroke = new SolidColorBrush(Color.FromArgb((byte)(150 - i * 25), 235, 255, 210)), StrokeThickness = 2, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-18) }, 150 - d / 2, 150 - d * 0.21);
                }
                Add(new Ellipse { Width = 76, Height = 76, Fill = Lin("#FFF2FFD9", "#FF5FBF6A") }, 112, 112);
                Add(new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Hex("#FFF2FFD9")) }, 232, 96);
                break;
            default: // tides
                Add(new Rectangle { Width = 300, Height = 300, Fill = Lin("#FF0B1E5B", "#FF22C9EA") }, 0, 0);
                for (int i = 0; i < 7; i++)
                {
                    double d = 140 + i * 46;
                    Add(new Ellipse { Width = d, Height = d, Stroke = new SolidColorBrush(Color.FromArgb((byte)(170 - i * 20), 255, 255, 255)), StrokeThickness = 2.4 }, 150 - d / 2, 250 - d / 2);
                }
                Add(new Ellipse { Width = 54, Height = 54, Fill = new SolidColorBrush(Hex("#FFF6FBFF")) }, 196, 52);
                break;
        }
        return c;
    }

    static BitmapSource Render(FrameworkElement v, int w, int h, double scale)
    {
        v.Measure(new Size(w, h));
        v.Arrange(new Rect(0, 0, w, h));
        v.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(v);
        rtb.Freeze();
        return rtb;
    }

    static void Save(BitmapSource b, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var f = File.Create(path);
        enc.Save(f);
    }

    static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);
}
