using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GlassPlayer;

// Liquid glass, ported to WPF from the techniques in liquid-glass-react (MIT, github.com/rdev/liquid-glass-react,
// whose shader is adapted from github.com/shuding/liquid-glass):
//   • refraction  - a rounded-rect SDF lens map pulls the backdrop toward the centre near the edges
//   • aberration  - red / green / blue are displaced by slightly different amounts at the edges
//   • elasticity  - the card leans and stretches toward the cursor when it is within 200 px
//   • light rim   - the border highlight's angle and strength follow the cursor
//   • over light  - over bright backgrounds the glass darkens and bends less
public partial class MainWindow
{
    const double DisplacementScale = 28;    // how far (design units) the rim bends the background inward
    const double LensBand = 32;             // width of the bevel that bends light, in design units
    const double AberrationIntensity = 2;
    const double Elasticity = 0.18;
    const double ActivationZone = 200;      // px around the card where the cursor still pulls

    float[]? _lensX, _lensY, _lensEdge;
    int _lensW, _lensH;
    double _lensCardW, _lensCardH;
    bool _overLight;

    // ---------- refraction + chromatic aberration (CPU, on the small backdrop) ----------

    static double SmoothStep(double a, double b, double t)
    {
        t = Math.Clamp((t - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static double RoundedRectSdf(double x, double y, double w, double h, double r)
    {
        double qx = Math.Abs(x) - w + r, qy = Math.Abs(y) - h + r;
        return Math.Min(Math.Max(qx, qy), 0) + Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) - r;
    }

    // Per backdrop pixel: where to sample from (dx, dy in backdrop pixels) and how "edgy" it is (0..1).
    void BuildLens(int bw, int bh, double cardW, double cardH)
    {
        if (_lensX != null && _lensW == bw && _lensH == bh && _lensCardW == cardW && _lensCardH == cardH) return;
        _lensW = bw; _lensH = bh; _lensCardW = cardW; _lensCardH = cardH;
        _lensX = new float[bw * bh]; _lensY = new float[bw * bh]; _lensEdge = new float[bw * bh];

        double fullW = cardW + 2 * Inflate, fullH = cardH + 2 * Inflate;
        double pxPerDipX = bw / fullW, pxPerDipY = bh / fullH;
        double s = cardW / DesignW;
        double hw = cardW / 2, hh = cardH / 2, rad = BaseRadius * s;
        double band = LensBand * s, pull = DisplacementScale * s;
        for (int y = 0; y < bh; y++)
        for (int x = 0; x < bw; x++)
        {
            // card-local position, from the centre (DIPs)
            double px = (x + 0.5) / pxPerDipX - Inflate - hw, py = (y + 0.5) / pxPerDipY - Inflate - hh;
            // signed distance to the rounded card edge + its outward normal
            double qx = Math.Abs(px) - (hw - rad), qy = Math.Abs(py) - (hh - rad);
            double sdf, nx, ny;
            if (qx > 0 && qy > 0) { double l = Math.Sqrt(qx * qx + qy * qy); sdf = l - rad; nx = qx / l * Math.Sign(px); ny = qy / l * Math.Sign(py); }
            else if (qx > qy) { sdf = qx - rad; nx = Math.Sign(px); ny = 0; }
            else { sdf = qy - rad; nx = 0; ny = Math.Sign(py); }
            double inside = -sdf;
            if (inside <= 0 || inside >= band) continue;
            // a thick-glass bevel: the closer to the rim, the further inward the light is bent
            double t = 1 - inside / band;
            double amount = pull * t * t;
            int k = y * bw + x;
            _lensX[k] = (float)(-nx * amount * pxPerDipX);
            _lensY[k] = (float)(-ny * amount * pxPerDipY);
            _lensEdge[k] = (float)SmoothStep(0.15, 0.85, t);
        }
    }

    byte[] Refract(byte[] src, int w, int h)
    {
        BuildLens(w, h, Card.ActualWidth > 0 ? Card.ActualWidth : DesignW, Card.ActualHeight > 0 ? Card.ActualHeight : DesignH);
        var dst = new byte[src.Length];
        double k = _overLight ? 0.5 : 1;                                         // bend less over light
        double gs = 1 - AberrationIntensity * 0.05, bs = 1 - AberrationIntensity * 0.1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int p = y * w + x, o = p * 4;
            float lx = _lensX![p], ly = _lensY![p], e = _lensEdge![p];
            if (lx == 0 && ly == 0) { dst[o] = src[o]; dst[o + 1] = src[o + 1]; dst[o + 2] = src[o + 2]; dst[o + 3] = 255; continue; }
            double ddx = lx * k, ddy = ly * k;
            // centre: one sample; edges: each channel bent by its own amount (chromatic aberration)
            double r = Sample(src, w, h, x + ddx, y + ddy, 2);
            double g = e > 0 ? Sample(src, w, h, x + ddx * gs, y + ddy * gs, 1) : Sample(src, w, h, x + ddx, y + ddy, 1);
            double b = e > 0 ? Sample(src, w, h, x + ddx * bs, y + ddy * bs, 0) : Sample(src, w, h, x + ddx, y + ddy, 0);
            dst[o + 2] = (byte)r; dst[o + 1] = (byte)g; dst[o] = (byte)b; dst[o + 3] = 255;
        }
        return dst;
    }

    static double Sample(byte[] px, int w, int h, double fx, double fy, int c)
    {
        fx = Math.Clamp(fx, 0, w - 1.001); fy = Math.Clamp(fy, 0, h - 1.001);
        int x0 = (int)fx, y0 = (int)fy;
        double tx = fx - x0, ty = fy - y0;
        int i00 = (y0 * w + x0) * 4 + c, i10 = i00 + 4, i01 = i00 + w * 4, i11 = i01 + 4;
        return (px[i00] * (1 - tx) + px[i10] * tx) * (1 - ty) + (px[i01] * (1 - tx) + px[i11] * tx) * ty;
    }

    // Over a bright background the glass darkens a little and bends less (liquid-glass-react's "overLight").
    void MeasureLight(byte[] px)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < px.Length; i += 16) { sum += 0.299 * px[i + 2] + 0.587 * px[i + 1] + 0.114 * px[i]; n++; }
        bool light = n > 0 && sum / n / 255 > 0.62;
        if (light == _overLight) return;
        _overLight = light;
        OverLightDim.BeginAnimation(OpacityProperty, new DoubleAnimation(light ? 0.22 : 0, TimeSpan.FromMilliseconds(300)));
    }

    // ---------- elasticity + cursor-following rim light ----------

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    readonly DispatcherTimer _mouse = new() { Interval = TimeSpan.FromMilliseconds(16) };
    double _sx = 1, _sy = 1, _tx, _ty, _mx, _my;   // current (smoothed) values

    void StartLiquidMouse()
    {
        _mouse.Tick += (_, _) => TrackCursor();
        IsVisibleChanged += (_, _) => { if (IsVisible) _mouse.Start(); else _mouse.Stop(); };
        if (IsVisible) _mouse.Start();
    }

    void TrackCursor()
    {
        if (!IsVisible || _showcase || !GetCursorPos(out var p)) return;
        var local = PointFromScreen(new Point(p.X, p.Y));
        double cw = Card.ActualWidth, ch = Card.ActualHeight;
        if (cw <= 0) return;
        double dx = local.X - (Pad + cw / 2), dy = local.Y - (Pad + ch / 2);
        ApplyLiquid(dx, dy, cw, ch, smooth: true);
    }

    // dx, dy: cursor relative to the card centre (DIPs). Same maths as liquid-glass-react's elastic transform.
    void ApplyLiquid(double dx, double dy, double cw, double ch, bool smooth)
    {
        double ex = Math.Max(0, Math.Abs(dx) - cw / 2), ey = Math.Max(0, Math.Abs(dy) - ch / 2);
        double edge = Math.Sqrt(ex * ex + ey * ey);
        double fade = edge > ActivationZone ? 0 : 1 - edge / ActivationZone;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        double tsx = 1, tsy = 1, ttx = 0, tty = 0, tmx = 0, tmy = 0;
        if (fade > 0 && dist > 0)
        {
            double nx = dx / dist, ny = dy / dist;
            double stretch = Math.Min(dist / 300, 1) * Elasticity * fade;
            tsx = Math.Max(0.8, 1 + Math.Abs(nx) * stretch * 0.3 - Math.Abs(ny) * stretch * 0.15);
            tsy = Math.Max(0.8, 1 + Math.Abs(ny) * stretch * 0.3 - Math.Abs(nx) * stretch * 0.15);
            ttx = dx * Elasticity * 0.1 * fade;
            tty = dy * Elasticity * 0.1 * fade;
            tmx = Math.Clamp(dx / cw * 100, -100, 100) * fade;   // mouse offset in %, like the original
            tmy = Math.Clamp(dy / ch * 100, -100, 100) * fade;
        }

        double a = smooth ? 0.22 : 1;   // ~0.2 s ease-out at 60 Hz
        double nsx = _sx + (tsx - _sx) * a, nsy = _sy + (tsy - _sy) * a, ntx = _tx + (ttx - _tx) * a, nty = _ty + (tty - _ty) * a;
        double nmx = _mx + (tmx - _mx) * a, nmy = _my + (tmy - _my) * a;
        bool moved = Math.Abs(nsx - _sx) > 0.0004 || Math.Abs(nsy - _sy) > 0.0004 || Math.Abs(ntx - _tx) > 0.05 || Math.Abs(nty - _ty) > 0.05
                     || Math.Abs(nmx - _mx) > 0.3 || Math.Abs(nmy - _my) > 0.3;
        if (!moved && smooth) return;   // settled: no redraw
        _sx = nsx; _sy = nsy; _tx = ntx; _ty = nty; _mx = nmx; _my = nmy;

        ElasticScale.ScaleX = _sx; ElasticScale.ScaleY = _sy;
        ElasticShift.X = _tx; ElasticShift.Y = _ty;
        UpdateRimLight(_mx, _my);
    }

    // The border gradient: 135° + mouseX·1.2°, its bright band slides with mouseY and grows with |mouseX|.
    void UpdateRimLight(double mx, double my)
    {
        double ang = (135 + mx * 1.2) * Math.PI / 180;
        double cx = Math.Cos(ang) / 2, cy = Math.Sin(ang) / 2;
        RimLightBrush.StartPoint = new Point(0.5 - cx, 0.5 - cy);
        RimLightBrush.EndPoint = new Point(0.5 + cx, 0.5 + cy);
        RimLight1.Offset = Math.Max(10, 33 + my * 0.3) / 100;
        RimLight2.Offset = Math.Min(90, 66 + my * 0.4) / 100;
        RimLight1.Color = Color.FromArgb((byte)Math.Clamp((0.30 + Math.Abs(mx) * 0.008) * 255, 0, 255), 255, 255, 255);
        RimLight2.Color = Color.FromArgb((byte)Math.Clamp((0.75 + Math.Abs(mx) * 0.012) * 255, 0, 255), 255, 255, 255);
    }
}
