using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace GlassPlayer;

public partial class MainWindow : Window
{
    const double DesignW = 340, DesignH = 200, BaseRadius = 30;
    const double Pad = 16;          // room around the card for its shadow
    const double Inflate = 28;      // backdrop is captured this far past the card so the blur has no dark edges
    const int LayoutVersion = 3;

    readonly Settings _settings = Settings.Load();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _glass = new() { Interval = TimeSpan.FromMilliseconds(650) };
    readonly DispatcherTimer _glassSoon = new() { Interval = TimeSpan.FromMilliseconds(16) };
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;

    TimeSpan _pos, _end;
    DateTimeOffset _stamp = DateTimeOffset.Now;
    double _rate = 1;
    bool _playing, _shuffle, _hasArt, _frontA = true;
    bool _spotifyUp, _wantVisible;
    bool? _iconPlaying;
    string _trackKey = "";
    ulong _lastBackdropHash;

    public MainWindow()
    {
        InitializeComponent();
        Grain.Fill = MakeGrain();
        BuildSpin();
        StartLiquidMouse();
        UpdateRimLight(0, 0);
        ApplySettings();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            // Hide the widget from screen capture so it can capture what is behind itself.
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        };
        Card.SizeChanged += (_, _) => UpdateCorners();
        MouseEnter += (_, _) => Fade(CloseBtn, 1);
        MouseLeave += (_, _) => { Fade(CloseBtn, 0); Cursor = null; Animate(HoverSheen, OpacityProperty, 0, 300); };
        MouseEnter += (_, _) => Animate(HoverSheen, OpacityProperty, 0.6, 200);
        PreviewMouseLeftButtonDown += (_, _) => Animate(HoverSheen, OpacityProperty, 1, 80);
        PreviewMouseLeftButtonUp += (_, _) => Animate(HoverSheen, OpacityProperty, 0.6, 250);
        Track.MouseEnter += (_, _) => { Animate(TrackBar, HeightProperty, 5.5, 160); Animate(Knob, WidthProperty, 13, 160); Animate(Knob, HeightProperty, 13, 160); };
        Track.MouseLeave += (_, _) => { Animate(TrackBar, HeightProperty, 3.5, 220); Animate(Knob, WidthProperty, 9, 220); Animate(Knob, HeightProperty, 9, 220); };
        Knob.SizeChanged += (_, _) => UpdateProgress();
        _tick.Tick += (_, _) => UpdateProgress();
        _watch.Tick += (_, _) => CheckSpotify();
        _glass.Tick += (_, _) => CaptureBackdrop();
        _glassSoon.Tick += (_, _) => { _glassSoon.Stop(); CaptureBackdrop(); };
        LocationChanged += (_, _) => GlassSoon();
        SizeChanged += (_, _) => GlassSoon();
        IsVisibleChanged += (_, _) => { if (IsVisible) { _lastBackdropHash = 0; CaptureBackdrop(); _glass.Start(); } else _glass.Stop(); };
        Closing += (_, _) => SaveSettings();
        Closed += (_, _) => Application.Current.Shutdown();
        SetPlayIcon(false);
    }

    Tray? _tray;

    public bool IsShown => IsVisible && _wantVisible;
    public void ShowFromTray() { ShowWidget(); Activate(); }
    public void ToggleFromTray() { if (IsShown) HideWidget(); else ShowFromTray(); }
    public void ToggleTopmost() { Topmost = !Topmost; SetPinVisual(); SaveSettings(); }
    public void Quit() => Close();

    public async void Start()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Startup.MigrateOldShortcut();
        _tray = new Tray(this);
        Closed += (_, _) => _tray?.Dispose();
        CheckSpotify();
        _watch.Start();
        try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { ShowIdle("Media controls unavailable"); return; }
        _mgr.SessionsChanged += (_, _) => Dispatcher.InvokeAsync(BindSession);
        BindSession();
        _tick.Start();

        // The widget is hidden from screenshots, so for checking the design it can render itself to a PNG.
        if (Environment.GetEnvironmentVariable("GLASSPLAYER_PROFILE") is string prof) StartProfile(prof);
        if (Environment.GetEnvironmentVariable("GLASSPLAYER_DEBUGSHOT") is string shot)
        {
            await Task.Delay(4000);
            var rtb = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);
            if ((_frontA ? ArtA : ArtB).Source is BitmapSource cover)
                File.WriteAllText(shot + ".txt", $"cover {cover.PixelWidth}x{cover.PixelHeight}");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var f = File.Create(shot);
            enc.Save(f);
        }
    }

    // ---------- follow Spotify: appear when it opens, leave when it closes ----------

    static bool IsSpotifyRunning()
    {
        var ps = Process.GetProcessesByName("Spotify");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    void CheckSpotify()
    {
        bool up = IsSpotifyRunning();
        if (up && !_spotifyUp) ShowWidget();
        else if (!up && _spotifyUp) HideWidget();
        _spotifyUp = up;
    }

    void ShowWidget()
    {
        _wantVisible = true;
        if (!IsVisible)
        {
            Root.Opacity = 0;
            Show();
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RootScale.ScaleX = RootScale.ScaleY = 0.88;
            Reveal();
        }
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var pop = new DoubleAnimation(1, TimeSpan.FromMilliseconds(560)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    void HideWidget()
    {
        _wantVisible = false;
        if (!IsVisible) return;
        SaveSettings();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => { if (!_wantVisible) Hide(); };
        Root.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    // ---------- media session ----------

    static bool IsSpotify(GlobalSystemMediaTransportControlsSession s) =>
        s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    void BindSession()
    {
        GlobalSystemMediaTransportControlsSession? next = null;
        try { next = _mgr?.GetSessions().FirstOrDefault(IsSpotify); } catch { }
        if (_session != null && next != null && _session.SourceAppUserModelId == next.SourceAppUserModelId) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMedia;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = next;
        _trackKey = "";
        if (_session == null) { ShowIdle(); return; }

        _session.MediaPropertiesChanged += OnMedia;
        _session.PlaybackInfoChanged += OnPlayback;
        _session.TimelinePropertiesChanged += OnTimeline;
        _ = RefreshMedia();
        RefreshPlayback();
        RefreshTimeline();
    }

    void OnMedia(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => { RefreshTimeline(); _ = RefreshMedia(); });
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => { RefreshPlayback(); RefreshTimeline(); });
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        Dispatcher.InvokeAsync(RefreshTimeline);

    void ShowIdle(string title = "Nothing on the deck")
    {
        TitleText.Text = title;
        ArtistText.Text = "Open Spotify to start";
        SetDeck(false);
        _playing = false; _end = TimeSpan.Zero; _hasArt = false;
        SetPlayIcon(false);
        SetShuffle(false, true);
        SetArt(null);
        UpdateProgress();
    }

    async Task RefreshMedia()
    {
        var s = _session;
        if (s == null) return;
        GlobalSystemMediaTransportControlsSessionMediaProperties p;
        try { p = await s.TryGetMediaPropertiesAsync(); } catch { return; }
        if (s != _session || p == null) return;

        string key = $"{p.Title}\n{p.Artist}\n{p.AlbumTitle}";
        bool changed = key != _trackKey;
        if (!changed && _hasArt) return;
        if (changed)
        {
            _trackKey = key;
            _hasArt = false;
            TitleText.Text = string.IsNullOrWhiteSpace(p.Title) ? "Nothing playing" : p.Title;
            ArtistText.Text = string.IsNullOrWhiteSpace(p.Artist) ? "Spotify" : p.Artist;
            TextPanel.BeginAnimation(OpacityProperty, Anim(0, 1, 420));
            TextShift.BeginAnimation(TranslateTransform.YProperty, Anim(8, 0, 520));
        }

        var art = p.Thumbnail == null ? null : await LoadThumb(p.Thumbnail);
        if (key != _trackKey) return;
        if (art != null) { _hasArt = true; SetArt(art); }
        else if (changed) SetArt(null);
    }

    void RefreshPlayback()
    {
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = null;
        try { info = _session?.GetPlaybackInfo(); } catch { }
        bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing != _playing) { _pos = CurrentPos(); _stamp = DateTimeOffset.Now; _playing = playing; }
        _rate = info?.PlaybackRate is double r && r > 0 ? r : 1;
        SetPlayIcon(playing);
        SetShuffle(info?.IsShuffleActive == true, info?.Controls.IsShuffleEnabled ?? true);
        SetDeck(playing);
    }

    void RefreshTimeline()
    {
        GlobalSystemMediaTransportControlsSessionTimelineProperties? t = null;
        try { t = _session?.GetTimelineProperties(); } catch { }
        if (t == null) return;
        _end = t.EndTime - t.StartTime;
        _pos = t.Position - t.StartTime;
        _stamp = t.LastUpdatedTime;
        if (_stamp == default || _stamp > DateTimeOffset.Now) _stamp = DateTimeOffset.Now;
        UpdateProgress();
    }

    TimeSpan CurrentPos()
    {
        var p = _pos;
        if (_playing) p += (DateTimeOffset.Now - _stamp) * _rate;
        if (p < TimeSpan.Zero) p = TimeSpan.Zero;
        if (_end > TimeSpan.Zero && p > _end) p = _end;
        return p;
    }

    void UpdateProgress()
    {
        if (!IsVisible) return;
        bool has = (_session != null || _showcase) && _end > TimeSpan.Zero;
        ProgressRow.Opacity = Track.Opacity = has ? 1 : 0.4;
        var pos = has ? CurrentPos() : TimeSpan.Zero;
        double w = has ? Math.Round(Math.Max(0, TrackBar.ActualWidth * Math.Clamp(pos / _end, 0, 1)) * 2) / 2 : 0;   // half-unit steps: fewer redraws
        TrackFill.Width = w; TrackGlow.Width = w;
        Knob.Margin = new Thickness(Math.Clamp(w - Knob.ActualWidth / 2, 0, Math.Max(0, TrackBar.ActualWidth - Knob.ActualWidth)), 0, 0, 0);
        PosText.Text = Fmt(pos);
        EndText.Text = "-" + Fmt(has ? _end - pos : TimeSpan.Zero);
    }

    static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---------- visuals ----------

    void SetPlayIcon(bool playing)
    {
        if (_iconPlaying == playing) return;
        bool first = _iconPlaying == null;
        _iconPlaying = playing;
        var (inIcon, inScale, outIcon) = playing ? (PauseIcon, PauseScale, PlayIcon) : (PlayIcon, PlayScale, PauseIcon);
        outIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(first ? 0 : 140)));
        inIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(first ? 0 : 220)));
        if (first) return;
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(380))
        { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 } };
        inScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        inScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // Toggles stay pure white like the reference: bright + dot when on, dimmed when off.
    void SetShuffle(bool on, bool supported)
    {
        _shuffle = on;
        ShuffleDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ShuffleBtn.Opacity = !supported ? 0.3 : on ? 1 : 0.6;
    }

    void SetPinVisual()
    {
        PinDot.Visibility = Topmost ? Visibility.Visible : Visibility.Collapsed;
        PinGlyph.Text = Topmost ? "" : "";
        PinBtn.Opacity = Topmost ? 1 : 0.6;
    }

    void SetArt(BitmapSource? art)
    {
        var (artIn, artOut) = _frontA ? (ArtB, ArtA) : (ArtA, ArtB);
        artIn.Source = art;
        Panel.SetZIndex(artIn, 2); Panel.SetZIndex(artOut, 1);
        Fade(artIn, art == null ? 0 : 1, 520); Fade(artOut, 0, 520);
        _frontA = !_frontA;

        // The record's label is the cover; the whole card takes its accent from the cover.
        RecordLabel.Fill = art == null ? (Brush)FindResource("Accent") : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        SetAccent(art == null ? DefaultAccent : Palette(art));

        if (art != null)
        {
            var pop = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            ArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    static async Task<BitmapSource?> LoadThumb(IRandomAccessStreamReference r)
    {
        try
        {
            using var ras = await r.OpenReadAsync();
            using var s = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    void UpdateCorners()
    {
        double s = Card.ActualWidth / DesignW, r = BaseRadius * s;
        Card.Clip = new RectangleGeometry(new Rect(Card.RenderSize), r, r);
        Shadow.CornerRadius = new CornerRadius(r);
        // Cover: whole-pixel position and size over the cover slot (22,22 / 72x72 / radius 18 in the 340x200 design).
        double ax = Math.Round(22 * s), asz = Math.Round(84 * s), ar = 14 * s;
        ArtCrisp.Margin = new Thickness(ax, ax, 0, 0);
        ArtCrisp.Width = ArtCrisp.Height = asz;
        ArtCrisp.Clip = new RectangleGeometry(new Rect(0, 0, asz, asz), ar, ar);
        ArtCrisp.RenderTransform = ArtScale;   // shares the little "pop" on track change
        ArtRim.CornerRadius = new CornerRadius(ar);
        // Hairlines stay exactly 1px; the soft glow and fringe grow with the card.
        RimOuter.CornerRadius = new CornerRadius(r);
        RimLight.CornerRadius = new CornerRadius(r);
        RimInner.CornerRadius = new CornerRadius(Math.Max(0, r - 1));
        RimGlow.CornerRadius = new CornerRadius(r);
        RimGlow.BorderThickness = new Thickness(9 * s);
        RimGlowBlur.Radius = 9 * s;
        RimFringe.CornerRadius = new CornerRadius(Math.Max(0, r - 1.5));
        RimFringe.BorderThickness = new Thickness(2 * s);
        RimFringeBlur.Radius = 2.5 * s;
    }

    // ---------- our own frosted glass ----------
    // Grab the screen under the card (the widget itself is excluded from capture), shrink it,
    // blur it on the CPU (tiny image, so it's cheap) and show it stretched behind the card.

    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int Downscale = 2, BlurRadius = 1, BlurPasses = 2;   // light frost, so the refraction stays visible

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    void GlassSoon() { if (!_glassSoon.IsEnabled) _glassSoon.Start(); }

    // Profiling (env GLASSPLAYER_PROFILE=<txt path>): where the CPU goes over 15 seconds.
    static readonly Stopwatch _capWatch = new();
    static int _caps, _capRedraws;

    async void StartProfile(string path)
    {

        await Task.Delay(15000);
        File.WriteAllText(path, $"captures {_caps}, redraws {_capRedraws}, capture ms total {_capWatch.ElapsedMilliseconds}, avg {(_caps == 0 ? 0 : _capWatch.ElapsedMilliseconds / (double)_caps):F1}");
    }

    void CaptureBackdrop()
    {
        _capWatch.Start(); _caps++;
        try { CaptureBackdropCore(); } finally { _capWatch.Stop(); }
    }

    void CaptureBackdropCore()
    {
        if (!IsVisible || _showcase) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr)) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int off = (int)Math.Round((Pad - Inflate) * dpi);
        int x = wr.Left + off, y = wr.Top + off;
        int w = wr.Right - wr.Left - 2 * off, h = wr.Bottom - wr.Top - 2 * off;
        int sw = Math.Max(4, w / Downscale), sh = Math.Max(4, h / Downscale);

        var px = new byte[sw * sh * 4];
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, sw, sh);
        try
        {
            var old = SelectObject(mem, bmp);
            SetStretchBltMode(mem, 3);                                   // COLORONCOLOR: fast shrink; the blur smooths it
            StretchBlt(mem, 0, 0, sw, sh, screen, x, y, w, h, 0x00CC0020); // SRCCOPY
            SelectObject(mem, old);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = sw, biHeight = -sh, biPlanes = 1, biBitCount = 32 };
            GetDIBits(mem, bmp, 0, (uint)sh, px, ref bi, 0);
        }
        finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }

        // Nothing changed behind us? Skip the redraw (keeps CPU near zero on a still desktop).
        ulong hash = 1469598103934665603UL ^ (ulong)(sw * 31 + sh);
        for (int i = 0; i < px.Length; i += 12) hash = (hash ^ px[i] ^ ((ulong)px[i + 1] << 8)) * 1099511628211UL;
        if (hash == _lastBackdropHash) return;
        _lastBackdropHash = hash; _capRedraws++;
        ApplyBackdrop(px, sw, sh);
    }

    // Blur + vibrance a small BGRA copy of what's behind the card and show it as the glass.
    void ApplyBackdrop(byte[] px, int sw, int sh)
    {
        MeasureLight(px);
        for (int p = 0; p < BlurPasses; p++) { BoxBlur(px, sw, sh, BlurRadius, true); BoxBlur(px, sw, sh, BlurRadius, false); }
        px = Refract(px, sw, sh);
        Vibrance(px);

        var src = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgr32, null, px, sw * 4);
        src.Freeze();
        Backdrop.Source = src;
    }

    // One separable box-blur pass (3 passes ≈ Gaussian).
    static void BoxBlur(byte[] px, int w, int h, int r, bool horizontal)
    {
        int len = horizontal ? w : h, lines = horizontal ? h : w;
        var line = new int[len * 3];
        int div = 2 * r + 1;
        for (int l = 0; l < lines; l++)
        {
            for (int i = 0; i < len; i++)
            {
                int idx = horizontal ? (l * w + i) * 4 : (i * w + l) * 4;
                line[i * 3] = px[idx]; line[i * 3 + 1] = px[idx + 1]; line[i * 3 + 2] = px[idx + 2];
            }
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += line[Math.Clamp(k, 0, len - 1) * 3 + c];
                for (int i = 0; i < len; i++)
                {
                    int idx = horizontal ? (l * w + i) * 4 : (i * w + l) * 4;
                    px[idx + c] = (byte)(sum / div);
                    sum += line[Math.Min(i + r + 1, len - 1) * 3 + c] - line[Math.Max(i - r, 0) * 3 + c];
                }
            }
        }
    }

    // A touch more colour, like Apple's vibrancy.
    static void Vibrance(byte[] px)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            double lum = 0.299 * r + 0.587 * g + 0.114 * b;
            px[i] = (byte)Math.Clamp(lum + (b - lum) * 1.4, 0, 255);
            px[i + 1] = (byte)Math.Clamp(lum + (g - lum) * 1.4, 0, 255);
            px[i + 2] = (byte)Math.Clamp(lum + (r - lum) * 1.4, 0, 255);
            px[i + 3] = 255;
        }
    }

    static DoubleAnimation Anim(double from, double to, int ms) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    static void Animate(UIElement el, DependencyProperty dp, double to, int ms) =>
        el.BeginAnimation(dp, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    static void Fade(UIElement el, double to, int ms = 220)
    {
        Animate(el, OpacityProperty, to, ms);
        if (el is Button) el.IsHitTestVisible = to > 0;
    }

    // ---------- input ----------

    static async void Fire(Func<Task> f) { try { await f(); } catch { } }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        var s = _session;
        if (s == null) { OpenSpotify(); return; }
        SetPlayIcon(!_playing);
        Fire(async () => await s.TryTogglePlayPauseAsync());
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        var s = _session;
        if (s != null) Fire(async () => await s.TrySkipNextAsync());
    }

    void Prev_Click(object sender, RoutedEventArgs e)
    {
        var s = _session;
        if (s != null) Fire(async () => await s.TrySkipPreviousAsync());
    }

    void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        var s = _session;
        if (s == null) return;
        bool want = !_shuffle;
        SetShuffle(want, true);
        Fire(async () => await s.TryChangeShuffleActiveAsync(want));
    }

    void Pin_Click(object sender, RoutedEventArgs e) => ToggleTopmost();

    // × hides until Spotify is opened again; Shift+× quits Glass Player completely.
    void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Close();
        else HideWidget();
    }

    void Art_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenSpotify();
    }

    void Track_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var s = _session;
        if (s == null || _end <= TimeSpan.Zero || TrackBar.ActualWidth <= 0) return;
        double frac = Math.Clamp(e.GetPosition(TrackBar).X / TrackBar.ActualWidth, 0, 1);
        var target = TimeSpan.FromTicks((long)(_end.Ticks * frac));
        TimeSpan start = TimeSpan.Zero;
        try { start = s.GetTimelineProperties().StartTime; } catch { }
        _pos = target; _stamp = DateTimeOffset.Now;
        UpdateProgress();
        Fire(async () => await s.TryChangePlaybackPositionAsync((start + target).Ticks));
    }

    static void OpenSpotify()
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); } catch { }
    }

    // ---------- drag + resize from the card's edges ----------

    int EdgeAt(Point p)
    {
        const double B = 8, C = 26;   // edge band, corner zone (measured from the card, not the shadow margin)
        double x = p.X - Pad, y = p.Y - Pad, w = ActualWidth - 2 * Pad, h = ActualHeight - 2 * Pad;
        bool l = x < C, r = x > w - C, t = y < C, b = y > h - C;
        if (t && l && (x < B * 2 || y < B * 2)) return 4;
        if (t && r && (x > w - B * 2 || y < B * 2)) return 5;
        if (b && l && (x < B * 2 || y > h - B * 2)) return 7;
        if (b && r && (x > w - B * 2 || y > h - B * 2)) return 8;
        if (x < B) return 1;
        if (x > w - B) return 2;
        if (y < B) return 3;
        if (y > h - B) return 6;
        return 0;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = EdgeAt(e.GetPosition(this)) switch
        {
            1 or 2 => Cursors.SizeWE,
            3 or 6 => Cursors.SizeNS,
            4 or 8 => Cursors.SizeNWSE,
            5 or 7 => Cursors.SizeNESW,
            _ => null,
        };
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState != MouseButtonState.Pressed) return;
        int edge = EdgeAt(e.GetPosition(this));
        if (edge == 0) { DragMove(); return; }
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, 0x0112, (IntPtr)(0xF000 + edge), IntPtr.Zero); // SC_SIZE
    }

    // ---------- window: aspect-locked resize + saved placement ----------

    const int WM_SIZING = 0x0214, WM_EXITSIZEMOVE = 0x0232;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SIZING) { KeepAspect(wParam.ToInt32(), lParam); handled = true; return (IntPtr)1; }
        if (msg == WM_EXITSIZEMOVE) { SaveSettings(); _lastBackdropHash = 0; CaptureBackdrop(); }
        return IntPtr.Zero;
    }

    void KeepAspect(int edge, IntPtr lParam)
    {
        var rc = Marshal.PtrToStructure<RECT>(lParam);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double pad = 2 * Pad * dpi, ratio = DesignW / DesignH;
        double w = rc.Right - rc.Left - pad, h = rc.Bottom - rc.Top - pad;
        if (edge == 3 || edge == 6) w = h * ratio;          // top / bottom edge drives height
        w = Math.Clamp(w, 260 * dpi, 900 * dpi);
        h = w / ratio;
        int W = (int)Math.Round(w + pad), H = (int)Math.Round(h + pad);
        if (edge == 1 || edge == 4 || edge == 7) rc.Left = rc.Right - W; else rc.Right = rc.Left + W;
        if (edge == 3 || edge == 4 || edge == 5) rc.Top = rc.Bottom - H; else rc.Bottom = rc.Top + H;
        Marshal.StructureToPtr(rc, lParam, false);
    }

    void ApplySettings()
    {
        // Saved sizes from older layouts belong to a different shape, so only reuse ours.
        double card = _settings.Layout == LayoutVersion && _settings.Width is double sw && sw - 2 * Pad >= 260 ? sw - 2 * Pad : DesignW;
        Width = card + 2 * Pad;
        Height = card / (DesignW / DesignH) + 2 * Pad;

        var wa = SystemParameters.WorkArea;
        double left = _settings.Left ?? wa.Right - Width;
        double top = _settings.Top ?? wa.Bottom - Height;
        if (_settings.Layout != LayoutVersion && _settings.Left is double ol && _settings.Top is double ot) { left = ol - Pad; top = ot - Pad; }
        bool onScreen =
            left > SystemParameters.VirtualScreenLeft - Width + 60 &&
            top > SystemParameters.VirtualScreenTop - 20 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
        if (!onScreen) { left = wa.Right - Width; top = wa.Bottom - Height; }
        Left = left; Top = top;

        Topmost = _settings.Topmost;
        SetPinVisual();
    }

    void SaveSettings()
    {
        if ((!IsLoaded && !IsVisible) || _showcase) return;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = ActualWidth > 0 ? ActualWidth : Width;
        _settings.Layout = LayoutVersion;
        _settings.Topmost = Topmost;
        _settings.Save();
    }
}
