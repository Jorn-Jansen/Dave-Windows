using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DaveWindows;

/// <summary>Dave's colours: the purple/cyan of the screen glow and the bubble.</summary>
internal static class Palette
{
    public static readonly Color Purple = Color.FromArgb(150, 70, 255);
    public static readonly Color Violet = Color.FromArgb(110, 90, 255);
    public static readonly Color Pink = Color.FromArgb(220, 80, 230);
    public static readonly Color Cyan = Color.FromArgb(0, 210, 255);

    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);
}

/// <summary>
/// The bubble at the bottom of the screen: a round microphone while Dave listens, which morphs into a pill
/// with the answer. Comes with a soft purple/cyan glow around the edges of the main monitor, like Siri.
/// Never takes focus away from your game or app.
/// </summary>
public class Bubble : LayeredWindow
{
    private const int HaloMargin = 30;           // room around the shape for its halo
    private const float CircleSize = 88f;
    private const int MaxTextWidth = 860;
    private static readonly Font TextFont = new("Segoe UI", 21f, FontStyle.Regular, GraphicsUnit.Pixel);

    private readonly Glow glow = new();
    private readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer hideTimer = new();
    private readonly DateTime started = DateTime.Now;
    private Surface? surface;

    private bool listening;
    private string text = "";
    private SizeF size = new(CircleSize, CircleSize), targetSize = new(CircleSize, CircleSize);
    private float contentAlpha;   // text / microphone fade-in, 0..1
    private float opacity, targetOpacity;
    private int frame;

    public event Action? Clicked;

    public Bubble() : base(clickThrough: false)
    {
        _ = glow.Handle; // create it on this (UI) thread
        Vision.HiddenFromScreenshots.Add(this);
        Vision.HiddenFromScreenshots.Add(glow);
        frameTimer.Tick += (_, _) => Tick();
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); FadeOut(); };
        MouseUp += (_, _) => Clicked?.Invoke();
    }

    /// <summary>The round microphone: Dave is listening.</summary>
    public void ShowListening()
    {
        if (InvokeRequired) { BeginInvoke(ShowListening); return; }
        if (!listening) contentAlpha = 0;
        listening = true;
        text = "";
        targetSize = new SizeF(CircleSize, CircleSize);
        Appear();
    }

    /// <summary>The pill with text: Dave's answer, what you said, or what he's doing.</summary>
    public void ShowText(string newText)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowText(newText)); return; }
        if (listening || newText != text) contentAlpha = 0;
        listening = false;
        text = newText;
        var measured = Measure(newText);
        targetSize = new SizeF(Math.Max(measured.Width + 56, 120), Math.Max(measured.Height + 30, 64));
        Appear();
    }

    public void HideAfter(int ms)
    {
        if (InvokeRequired) { BeginInvoke(() => HideAfter(ms)); return; }
        hideTimer.Stop();
        hideTimer.Interval = Math.Max(1, ms);
        hideTimer.Start();
    }

    private void Appear()
    {
        hideTimer.Stop();
        if (!Visible)
        {
            // Start from a small circle so the first shape grows in.
            size = new SizeF(CircleSize * 0.6f, CircleSize * 0.6f);
            Show();
        }
        targetOpacity = 1;
        glow.FadeIn();
        frameTimer.Start();
    }

    private void FadeOut()
    {
        targetOpacity = 0;
        glow.FadeOut();
    }

    private static SizeF Measure(string s)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g.MeasureString(s, TextFont, MaxTextWidth);
    }

    // --- Animation ---

    private void Tick()
    {
        frame++;
        float t = (float)(DateTime.Now - started).TotalSeconds;

        // Ease the shape towards its target (circle <-> pill), then fade the content in.
        size = new SizeF(size.Width + (targetSize.Width - size.Width) * 0.2f, size.Height + (targetSize.Height - size.Height) * 0.2f);
        bool settled = Math.Abs(size.Width - targetSize.Width) < 10 && Math.Abs(size.Height - targetSize.Height) < 6;
        if (settled) contentAlpha = Math.Min(1, contentAlpha + 0.12f);
        opacity += (targetOpacity - opacity) * 0.2f;

        if (targetOpacity == 0 && opacity < 0.02f)
        {
            opacity = 0;
            frameTimer.Stop();
            Hide();
            glow.Hide();
            return;
        }

        Render(t);
        if (frame % 3 == 0) glow.Render(t, listening ? Recorder.Level : 0); // the big glow at ~20 fps is plenty
    }

    private void Render(float t)
    {
        int width = (int)Math.Ceiling(size.Width) + HaloMargin * 2, height = (int)Math.Ceiling(size.Height) + HaloMargin * 2;
        if (surface == null || surface.Width != width || surface.Height != height)
        {
            surface?.Dispose();
            surface = new Surface(width, height);
        }
        var g = surface.Graphics;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var shape = new RectangleF(HaloMargin, HaloMargin, size.Width, size.Height);
        float radius = size.Height <= 96 ? Math.Min(size.Width, size.Height) / 2f : 34f;
        float level = listening ? Recorder.Level : 0;
        float angle = 25 + 35 * MathF.Sin(t * 0.9f);

        // Soft halo in the glow colours
        for (int i = 4; i >= 1; i--)
        {
            var halo = RectangleF.Inflate(shape, i * 5 + level * 10, i * 5 + level * 10);
            using var haloPath = Rounded(halo, radius + i * 5);
            using var haloBrush = Gradient(halo, angle, (int)((22 + level * 40) / i * 2));
            g.FillPath(haloBrush, haloPath);
        }

        using var path = Rounded(shape, radius);
        using (var fill = Gradient(shape, angle, 245)) g.FillPath(fill, path);
        if (!listening)
        {
            // A little darker behind text so it stays readable on the bright gradient.
            using var shade = new SolidBrush(Color.FromArgb(60, 12, 8, 40));
            g.FillPath(shade, path);
        }
        using (var border = new Pen(Color.FromArgb(110, 255, 255, 255), 1.5f)) g.DrawPath(border, path);

        if (listening) DrawMicrophone(g, shape, level, t);
        else DrawText(g, shape);

        Present(surface, BottomCenter(width, height), (byte)(opacity * 255));
    }

    private Point BottomCenter(int width, int height)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        return new Point(area.Left + (area.Width - width) / 2, area.Bottom - 40 - height + HaloMargin);
    }

    private void DrawMicrophone(Graphics g, RectangleF shape, float level, float t)
    {
        var alpha = (int)(255 * contentAlpha);
        float cx = shape.X + shape.Width / 2, cy = shape.Y + shape.Height / 2;

        // Pulsing ring: breathes gently, and grows when you talk.
        float ring = shape.Width / 2 - 7 + 3 * MathF.Sin(t * 3) + level * 6;
        using (var ringPen = new Pen(Color.FromArgb((int)(alpha * (0.35f + level * 0.6f)), 255, 255, 255), 2f))
            g.DrawEllipse(ringPen, cx - ring, cy - ring, ring * 2, ring * 2);

        using var white = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        using var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using (var capsule = Rounded(new RectangleF(cx - 8, cy - 22, 16, 27), 8)) g.FillPath(white, capsule);
        g.DrawArc(pen, cx - 14, cy - 12, 28, 24, 0, 180);  // the holder
        g.DrawLine(pen, cx, cy + 12, cx, cy + 19);          // stand
        g.DrawLine(pen, cx - 8, cy + 19, cx + 8, cy + 19);  // base
    }

    private void DrawText(Graphics g, RectangleF shape)
    {
        var alpha = (int)(255 * contentAlpha);
        if (alpha <= 0) return;
        var area = new RectangleF(shape.X + 28, shape.Y + 15, shape.Width - 56 + 4, shape.Height - 30 + 4);
        using var shadow = new SolidBrush(Color.FromArgb(alpha / 3, 0, 0, 0));
        using var white = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        g.DrawString(text, TextFont, shadow, new RectangleF(area.X + 1, area.Y + 1, area.Width, area.Height));
        g.DrawString(text, TextFont, white, area);
    }

    private static LinearGradientBrush Gradient(RectangleF rect, float angle, int alpha)
    {
        var brush = new LinearGradientBrush(RectangleF.Inflate(rect, 1, 1), Color.Black, Color.Black, angle);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = new[] { Palette.WithAlpha(Palette.Purple, alpha), Palette.WithAlpha(Palette.Violet, alpha), Palette.WithAlpha(Palette.Cyan, alpha) },
            Positions = new[] { 0f, 0.5f, 1f },
        };
        return brush;
    }

    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frameTimer.Dispose();
            hideTimer.Dispose();
            surface?.Dispose();
            glow.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The soft purple/cyan glow along the edges of the main monitor. Click-through: it never blocks the mouse.</summary>
public class Glow : LayeredWindow
{
    private Surface? surface;
    private float opacity, targetOpacity;

    public Glow() : base(clickThrough: true) { }

    public void FadeIn()
    {
        targetOpacity = 1;
        if (!Visible) Show();
    }

    public void FadeOut() => targetOpacity = 0;

    /// <summary>Draw one frame. [level] is how loud you're talking (0..1): the glow gets stronger with your voice.</summary>
    public void Render(float t, float level)
    {
        opacity += (targetOpacity - opacity) * 0.25f;
        if (!Visible) return;
        var bounds = Screen.PrimaryScreen!.Bounds;
        if (surface == null || surface.Width != bounds.Width || surface.Height != bounds.Height)
        {
            surface?.Dispose();
            surface = new Surface(bounds.Width, bounds.Height);
        }
        var g = surface.Graphics;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, bounds.Width, bounds.Height);

        // Soft blobs that drift around the edges, so the colours sway instead of sitting still.
        var blobs = new[] { (Palette.Purple, 0.00f, 0.031f), (Palette.Cyan, 0.36f, -0.024f), (Palette.Pink, 0.70f, 0.019f) };
        foreach (var (color, phase, speed) in blobs)
        {
            var p = Perimeter(rect, phase + t * speed + 0.04f * MathF.Sin(t * 0.7f + phase * 9));
            float r = 220 + 60 * MathF.Sin(t * 1.1f + phase * 7) + level * 120;
            using var blobPath = new GraphicsPath();
            blobPath.AddEllipse(p.X - r, p.Y - r, r * 2, r * 2);
            using var blob = new PathGradientBrush(blobPath)
            {
                CenterColor = Palette.WithAlpha(color, (int)(80 + level * 80)),
                SurroundColors = new[] { Color.Transparent },
            };
            g.FillPath(blob, blobPath);
        }

        // The glowing edge itself: soft wave bands that undulate along the edges like liquid, each at its own pace.
        // Wide and faint on the inside, thin and bright near the edge; they get bigger while you talk.
        float breathe = 0.9f + 0.1f * MathF.Sin(t * 1.3f);
        foreach (var band in Bands)
        {
            float depth = band.Depth * breathe * (1 + level * 0.6f);
            float swell = band.Swell * (1 + level * 1.3f);
            using var ring = WaveRing(rect, depth, swell, band, t);
            using var brush = new LinearGradientBrush(rect, Color.Black, Color.Black, (t * 30 + band.ColourShift) % 360f);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Palette.WithAlpha(Palette.Purple, band.Alpha), Palette.WithAlpha(Palette.Cyan, band.Alpha), Palette.WithAlpha(Palette.Pink, band.Alpha),
                    Palette.WithAlpha(Palette.Cyan, band.Alpha), Palette.WithAlpha(Palette.Purple, band.Alpha),
                },
                Positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f },
            };
            g.FillPath(brush, ring);
        }

        Present(surface, bounds.Location, (byte)(Math.Clamp(opacity, 0, 1) * 255));
        if (targetOpacity == 0 && opacity < 0.02f) Hide();
    }

    /// <summary>One wave band: how deep it reaches in, how much its inner edge swells, and the waves that move it.</summary>
    private record Band(float Depth, float Swell, int Alpha, float ColourShift, (float count, float speed, float weight)[] Waves);

    private static readonly Band[] Bands =
    {
        new(118, 46, 14, 0,   new[] { (3f, 0.55f, 0.55f), (5f, -0.8f, 0.3f), (8f, 1.2f, 0.15f) }),
        new(78,  32, 30, 40,  new[] { (4f, -0.75f, 0.5f), (7f, 1.05f, 0.32f), (11f, -1.5f, 0.18f) }),
        new(50,  20, 66, 80,  new[] { (5f, 0.95f, 0.5f), (9f, -1.35f, 0.3f), (14f, 1.8f, 0.2f) }),
        new(20,  8, 150, 120, new[] { (6f, -1.15f, 0.5f), (12f, 1.6f, 0.3f), (18f, -2.2f, 0.2f) }),
    };

    /// <summary>
    /// The area between the screen edge and a wavy, rounded inner edge [depth] px in, whose distance
    /// breathes by up to [swell] px following the band's waves (which travel around the screen over time).
    /// </summary>
    private static GraphicsPath WaveRing(Rectangle screen, float depth, float swell, Band band, float t)
    {
        const int Points = 150;
        float radius = depth * 1.2f + 40;
        var inner = RectangleF.Inflate(screen, -depth, -depth);
        var curve = new PointF[Points];
        for (int i = 0; i < Points; i++)
        {
            float u = i / (float)Points;
            var (p, n) = RoundedPoint(inner, radius, u);
            float wave = 0;
            foreach (var (count, speed, weight) in band.Waves)
                wave += weight * MathF.Sin(MathF.Tau * count * u + t * speed * 2.2f + count);
            // wave is about -1..1: positive pulls the edge further in, negative lets it recede towards the border
            curve[i] = new PointF(p.X + n.X * wave * swell, p.Y + n.Y * wave * swell);
        }
        var path = new GraphicsPath(FillMode.Alternate);
        path.AddRectangle(RectangleF.Inflate(screen, 2, 2));
        path.AddClosedCurve(curve, 0.5f);
        return path;
    }

    /// <summary>A point on a rounded rectangle, [u] going once around (0..1), with its inward normal.</summary>
    private static (PointF point, PointF normal) RoundedPoint(RectangleF r, float radius, float u)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        float straightX = r.Width - 2 * radius, straightY = r.Height - 2 * radius, arc = MathF.PI * radius / 2;
        float total = 2 * straightX + 2 * straightY + 4 * arc;
        float d = (u - MathF.Floor(u)) * total;

        (PointF, PointF) Corner(float cx, float cy, float startAngle, float along)
        {
            float a = startAngle + along / radius;
            var dir = new PointF(MathF.Cos(a), MathF.Sin(a));
            return (new PointF(cx + dir.X * radius, cy + dir.Y * radius), new PointF(-dir.X, -dir.Y));
        }

        if (d < straightX) return (new PointF(r.Left + radius + d, r.Top), new PointF(0, 1));                    // top
        d -= straightX;
        if (d < arc) return Corner(r.Right - radius, r.Top + radius, -MathF.PI / 2, d);                           // top-right
        d -= arc;
        if (d < straightY) return (new PointF(r.Right, r.Top + radius + d), new PointF(-1, 0));                   // right
        d -= straightY;
        if (d < arc) return Corner(r.Right - radius, r.Bottom - radius, 0, d);                                    // bottom-right
        d -= arc;
        if (d < straightX) return (new PointF(r.Right - radius - d, r.Bottom), new PointF(0, -1));                // bottom
        d -= straightX;
        if (d < arc) return Corner(r.Left + radius, r.Bottom - radius, MathF.PI / 2, d);                          // bottom-left
        d -= arc;
        if (d < straightY) return (new PointF(r.Left, r.Bottom - radius - d), new PointF(1, 0));                  // left
        d -= straightY;
        return Corner(r.Left + radius, r.Top + radius, MathF.PI, d);                                              // top-left
    }

    /// <summary>A point on the screen's edge, [u] going once around (0..1), a little inside the edge.</summary>
    private static PointF Perimeter(Rectangle r, float u)
    {
        u -= MathF.Floor(u);
        float w = r.Width, h = r.Height, d = u * 2 * (w + h), inset = 40;
        if (d < w) return new PointF(d, inset);
        d -= w;
        if (d < h) return new PointF(w - inset, d);
        d -= h;
        if (d < w) return new PointF(w - d, h - inset);
        d -= w;
        return new PointF(inset, h - d);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) surface?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A borderless, always-on-top window drawn with per-pixel transparency (soft edges and glows). Never takes focus.</summary>
public class LayeredWindow : Form
{
    private readonly bool clickThrough;

    public LayeredWindow(bool clickThrough)
    {
        this.clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x00080000 /* LAYERED */ | 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
            if (clickThrough) p.ExStyle |= 0x00000020; /* TRANSPARENT: clicks go through */
            return p;
        }
    }

    protected void Present(Surface surface, Point location, byte opacity)
    {
        var position = new NativePoint { X = location.X, Y = location.Y };
        var size = new NativeSize { Width = surface.Width, Height = surface.Height };
        var source = new NativePoint();
        var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 /* per-pixel alpha */ };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref position, ref size, surface.DeviceContext, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
        IntPtr hdcSrc, ref NativePoint pprSrc, int crKey, ref BlendFunction pblend, int dwFlags);
}

/// <summary>A drawing surface whose pixels Windows can show directly (no copying each frame).</summary>
public sealed class Surface : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public IntPtr DeviceContext { get; }
    public Graphics Graphics { get; }
    private readonly Bitmap bitmap;
    private readonly IntPtr dib, previous;

    public Surface(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        var info = new BitmapInfo { Size = 40, Width = Width, Height = -Height /* top-down */, Planes = 1, BitCount = 32 };
        DeviceContext = CreateCompatibleDC(IntPtr.Zero);
        dib = CreateDIBSection(DeviceContext, ref info, 0, out var bits, IntPtr.Zero, 0);
        previous = SelectObject(DeviceContext, dib);
        bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
        Graphics = Graphics.FromImage(bitmap);
    }

    public void Dispose()
    {
        Graphics.Dispose();
        bitmap.Dispose();
        SelectObject(DeviceContext, previous);
        DeleteObject(dib);
        DeleteDC(DeviceContext);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int Colors; // one palette entry, unused for 32-bit
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
