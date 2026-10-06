using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DaveWindows;

/// <summary>
/// Dave's colours, from the chosen theme (Settings → Theme). The names are the roles in the default theme:
/// Purple = main colour, Violet = in between, Pink = accent, Cyan = the bright contrast colour. Deep = the dark glass.
/// </summary>
internal static class Palette
{
    public static Color Purple { get; private set; } = Color.FromArgb(150, 70, 255);
    public static Color Violet { get; private set; } = Color.FromArgb(110, 90, 255);
    public static Color Pink { get; private set; } = Color.FromArgb(220, 80, 230);
    public static Color Cyan { get; private set; } = Color.FromArgb(0, 210, 255);
    public static Color Deep { get; private set; } = Color.FromArgb(16, 10, 38);
    /// <summary>The old look (before 1.3): a bright gradient pill and a microphone, no orb.</summary>
    public static bool Legacy { get; private set; }

    public static void Use(string themeId)
    {
        var theme = Themes.Get(themeId);
        (Purple, Violet, Pink, Cyan, Deep, Legacy) = (theme.Main, theme.Middle, theme.Accent, theme.Bright, theme.Deep, theme.Legacy);
    }

    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    /// <summary>The middle colour, lightened: for soft light spots on glass.</summary>
    public static Color Bright() => Color.FromArgb((Violet.R + 255) / 2, (Violet.G + 255) / 2, (Violet.B + 255) / 2);
}

/// <summary>The themes to choose from in the settings.</summary>
public static class Themes
{
    public record Theme(string Id, string Name, string Description, Color Main, Color Middle, Color Accent, Color Bright, Color Deep, bool Legacy = false);

    private static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public static readonly Theme[] All =
    {
        new("aurora", "Aurora", "Swirling northern lights in dark glass. The new look.", C("#9646FF"), C("#6E5AFF"), C("#DC50E6"), C("#00D2FF"), C("#100A26")),
        new("ember", "Ember", "Glowing fire: red, orange and gold.", C("#FF4D2E"), C("#FF2E63"), C("#FF9F1C"), C("#FFD166"), C("#1C0A06")),
        new("toxic", "Toxic", "Radioactive green and lime.", C("#22C55E"), C("#16A34A"), C("#A3E635"), C("#2DD4BF"), C("#06160C")),
        new("ocean", "Ocean", "Deep sea blue and turquoise.", C("#1E6BFF"), C("#2448C8"), C("#00C2A8"), C("#5CE1FF"), C("#060E20")),
        new("sakura", "Sakura", "Cherry blossom pink and lavender.", C("#FF6FAE"), C("#C77DFF"), C("#FF9ECF"), C("#FFD1E6"), C("#1E0A18")),
        new("midnight", "Midnight", "Sleek silver and white, nothing loud.", C("#8EA0BC"), C("#5B6B85"), C("#C9D4E5"), C("#FFFFFF"), C("#0C0E14")),
        new("legacy", "Legacy", "The original Dave: a bright gradient bubble with a microphone.", C("#9646FF"), C("#6E5AFF"), C("#DC50E6"), C("#00D2FF"), C("#100A26"), Legacy: true),
    };

    public static Theme Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>
/// The bubble at the bottom of the screen: a round microphone while Dave listens, which morphs into a pill
/// with the answer. Comes with a soft purple/cyan glow around the edges of the main monitor, like Siri.
/// Never takes focus away from your game or app.
/// </summary>
public class Bubble : LayeredWindow
{
    private const int HaloMargin = 34;           // room around the shape for its halo
    private const float CircleSize = 92f;
    private const float OrbSize = 30f;           // Dave's little orb in front of the text
    private const float PillPadding = 18f, OrbGap = 14f;
    private const int MaxTextWidth = 860;
    private static readonly Font TextFont = new("Segoe UI", 21f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font EmojiFont = new("Segoe UI Emoji", 20f, FontStyle.Regular, GraphicsUnit.Pixel); // see BubbleText

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

    // Spoken answers appear word by word along with Dave's voice
    private int wordCount;
    private bool revealWaiting;              // shown, but the voice hasn't started yet
    private DateTime revealAsked, revealStart;
    private double revealSeconds;            // 0 = show everything

    public event Action? Clicked;

    /// <summary>When this says true (Dave's window is in front), the bubble and glow stay hidden: the window shows everything.</summary>
    public Func<bool>? Suppressed;

    private bool IsSuppressed => Suppressed?.Invoke() == true;

    public Bubble() : base(clickThrough: false)
    {
        _ = glow.Handle; // create it on this (UI) thread
        Vision.HiddenFromScreenshots.Add(this);
        Vision.HiddenFromScreenshots.Add(glow);
        frameTimer.Tick += (_, _) => Tick();
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); FadeOut(); };
        MouseUp += (_, _) => Clicked?.Invoke();
        Speaker.Started += duration =>
        {
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (!revealWaiting) return;
                revealWaiting = false;
                revealStart = DateTime.Now;
                revealSeconds = duration.TotalSeconds * 0.93;
            });
        };
    }

    /// <summary>The round microphone: Dave is listening.</summary>
    public void ShowListening()
    {
        if (InvokeRequired) { BeginInvoke(ShowListening); return; }
        if (!listening) contentAlpha = 0;
        listening = true;
        text = "";
        revealWaiting = false;
        revealSeconds = 0;
        targetSize = new SizeF(CircleSize, CircleSize);
        Appear();
    }

    /// <summary>
    /// The pill with text: Dave's answer, what you said, or what he's doing. With [spoken], the words appear one by one
    /// along with Dave's voice (all at once if he doesn't speak within a few seconds, e.g. in quiet mode).
    /// </summary>
    public void ShowText(string newText, bool spoken = false)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowText(newText, spoken)); return; }
        if (listening || newText != text) contentAlpha = 0;
        listening = false;
        text = newText;
        wordCount = newText.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        revealWaiting = spoken;
        revealAsked = DateTime.Now;
        revealSeconds = 0;
        var measured = Measure(newText);
        targetSize = new SizeF(Math.Max(measured.Width + PillPadding * 2 + OrbSize + OrbGap, 120), Math.Max(measured.Height + 30, 64));
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
        if (IsSuppressed) { Log.Write("Bubble kept away: Dave's window is in front"); return; } // the window shows this instead
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
        return BubbleText.Layout(g, s, TextFont, EmojiFont, MaxTextWidth).size;
    }

    // --- Animation ---

    private void Tick()
    {
        if (IsSuppressed)
        {
            // The window was opened while the bubble was showing: get out of the way
            opacity = targetOpacity = 0;
            frameTimer.Stop();
            Hide();
            glow.FadeOut();
            return;
        }
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
            return;
        }

        if (revealWaiting && DateTime.Now - revealAsked > TimeSpan.FromSeconds(3)) revealWaiting = false; // no voice came: show it all

        Render(t);
        glow.Level = listening ? Recorder.Level : Speaker.Level * 0.7f; // the glow (on its own thread) moves with you, and with Dave's voice
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
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var shape = new RectangleF(HaloMargin, HaloMargin, size.Width, size.Height);
        float radius = Math.Min(size.Height / 2f, 34f);
        float voice = listening ? Recorder.Level : Speaker.Level; // you while listening, Dave while he talks

        if (Palette.Legacy) DrawLegacy(g, shape, voice, t);
        else if (listening && size.Width < CircleSize * 1.4f) DrawListening(g, shape, voice, t);
        else DrawPill(g, shape, radius, voice, t);

        Present(surface, BottomCenter(width, height), (byte)(opacity * 255));
    }

    /// <summary>The Legacy theme: the original look, a bright gradient circle with a microphone that becomes a gradient pill.</summary>
    private void DrawLegacy(Graphics g, RectangleF shape, float level, float t)
    {
        float radius = size.Height <= 96 ? Math.Min(size.Width, size.Height) / 2f : 34f;
        float angle = 25 + 35 * MathF.Sin(t * 0.9f);
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
            using var shade = new SolidBrush(Color.FromArgb(60, 12, 8, 40)); // a little darker behind text, so it stays readable
            g.FillPath(shade, path);
        }
        using (var border = new Pen(Color.FromArgb(110, 255, 255, 255), 1.5f)) g.DrawPath(border, path);

        if (!listening) { DrawText(g, shape, legacy: true); return; }
        var alpha = (int)(255 * contentAlpha);
        float cx = shape.X + shape.Width / 2, cy = shape.Y + shape.Height / 2;
        float ring = shape.Width / 2 - 7 + 3 * MathF.Sin(t * 3) + level * 6; // pulsing ring: breathes, and grows when you talk
        using (var ringPen = new Pen(Color.FromArgb((int)(alpha * (0.35f + level * 0.6f)), 255, 255, 255), 2f))
            g.DrawEllipse(ringPen, cx - ring, cy - ring, ring * 2, ring * 2);
        using var white = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        using var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using (var capsule = Rounded(new RectangleF(cx - 8, cy - 22, 16, 27), 8)) g.FillPath(white, capsule);
        g.DrawArc(pen, cx - 14, cy - 12, 28, 24, 0, 180);
        g.DrawLine(pen, cx, cy + 12, cx, cy + 19);
        g.DrawLine(pen, cx - 8, cy + 19, cx + 8, cy + 19);
    }

    /// <summary>Listening: a living orb of light that swirls, and swirls harder and grows when you talk.</summary>
    private void DrawListening(Graphics g, RectangleF shape, float level, float t)
    {
        var circle = new RectangleF(shape.X, shape.Y, shape.Width, shape.Width);
        float cx = circle.X + circle.Width / 2, cy = circle.Y + circle.Height / 2, r = circle.Width / 2;

        // A soft glow around it in two colours, breathing, and bigger while you talk
        foreach (var (color, extra, phase) in new[] { (Palette.Purple, 22f, 0f), (Palette.Cyan, 12f, 1.6f) })
        {
            float glowSize = r + extra + 5 * MathF.Sin(t * 2.2f + phase) + level * 22;
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(cx - glowSize, cy - glowSize, glowSize * 2, glowSize * 2);
            using var glowBrush = new PathGradientBrush(glowPath)
            {
                CenterColor = Palette.WithAlpha(color, (int)(200 + level * 55)),
                SurroundColors = new[] { Color.Transparent },
                FocusScales = new PointF(0.7f, 0.7f),
            };
            g.FillPath(glowBrush, glowPath);
        }

        DrawOrb(g, circle, t, level, 1f);

        // A faint microphone, so it's clear Dave is listening
        var alpha = (int)(190 * contentAlpha);
        if (alpha <= 0) return;
        float s = r / 44f;
        using var white = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        using var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using (var capsule = Rounded(new RectangleF(cx - 6 * s, cy - 16 * s, 12 * s, 20 * s), 6 * s)) g.FillPath(white, capsule);
        g.DrawArc(pen, cx - 10.5f * s, cy - 8 * s, 21 * s, 17 * s, 0, 180);
        g.DrawLine(pen, cx, cy + 9 * s, cx, cy + 14 * s);
    }

    /// <summary>
    /// Swirling coloured light inside a dark glass ball (Dave's "face"): coloured blobs orbit each other,
    /// faster and bigger with [level], under a glossy highlight.
    /// </summary>
    private static void DrawOrb(Graphics g, RectangleF circle, float t, float level, float alpha)
    {
        float cx = circle.X + circle.Width / 2, cy = circle.Y + circle.Height / 2, r = circle.Width / 2;
        using (var dark = new SolidBrush(Palette.WithAlpha(Palette.Deep, (int)(245 * alpha)))) g.FillEllipse(dark, circle);

        using var round = new GraphicsPath();
        round.AddEllipse(circle);
        var state = g.Save();
        g.SetClip(round, CombineMode.Intersect);
        var blobs = new[] { (Palette.Purple, 0.0f, 1.25f), (Palette.Cyan, 2.1f, -1.0f), (Palette.Pink, 4.2f, 0.8f), (Palette.Violet, 1.1f, -1.55f) };
        foreach (var (color, phase, speed) in blobs)
        {
            float a = phase + t * speed * (1 + level * 1.6f);
            float orbit = r * (0.34f + 0.12f * MathF.Sin(t * 0.7f + phase) + level * 0.18f);
            float bx = cx + MathF.Cos(a) * orbit, by = cy + MathF.Sin(a * 1.13f) * orbit;
            float br = r * (0.6f + 0.08f * MathF.Sin(t * 1.7f + phase) + level * 0.28f); // smaller patches: the colours stay apart
            using var blobPath = new GraphicsPath();
            blobPath.AddEllipse(bx - br, by - br, br * 2, br * 2);
            using var blob = new PathGradientBrush(blobPath)
            {
                CenterColor = Palette.WithAlpha(color, (int)(255 * alpha)), SurroundColors = new[] { Color.Transparent }, FocusScales = new PointF(0.25f, 0.25f),
            };
            g.FillPath(blob, blobPath);
        }
        // Glossy highlight at the top, like light on glass
        using (var shine = new GraphicsPath())
        {
            shine.AddEllipse(circle.X + r * 0.28f, circle.Y + r * 0.1f, r * 1.05f, r * 0.62f);
            using var shineBrush = new PathGradientBrush(shine) { CenterColor = Color.FromArgb((int)(95 * alpha), 255, 255, 255), SurroundColors = new[] { Color.Transparent } };
            g.FillPath(shineBrush, shine);
        }
        g.Restore(state);
        using var rim = new Pen(Color.FromArgb((int)(140 * alpha), 255, 255, 255), Math.Max(1f, r / 30f));
        g.DrawEllipse(rim, circle);
    }

    /// <summary>
    /// The answer: a dark glass pill with a shining gradient edge that slowly runs around, a small living orb in front
    /// of the text, and a glow that pulses with Dave's voice.
    /// </summary>
    private void DrawPill(Graphics g, RectangleF shape, float radius, float level, float t)
    {
        float spin = (t * 45) % 360; // the colours run around the edge

        // Glow around the pill, stronger while Dave talks
        for (int i = 4; i >= 1; i--)
        {
            var halo = RectangleF.Inflate(shape, i * 4 + level * 9, i * 4 + level * 9);
            using var haloPath = Rounded(halo, radius + i * 4);
            using var haloBrush = Gradient(halo, spin, (int)((18 + level * 55) / i * 2));
            g.FillPath(haloBrush, haloPath);
        }

        using var path = Rounded(shape, radius);
        // Dark glass with a hint of colour
        using (var glass = new SolidBrush(Palette.WithAlpha(Palette.Deep, 238))) g.FillPath(glass, path);
        using (var tint = Gradient(shape, 20, 60)) g.FillPath(tint, path);
        // Light catching the top edge
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        var top = new RectangleF(shape.X, shape.Y, shape.Width, Math.Min(shape.Height / 2, 30));
        using (var sheen = new LinearGradientBrush(RectangleF.Inflate(top, 0, 1), Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            g.FillRectangle(sheen, top);
        // A soft spot of light that drifts along the pill
        float spotX = shape.X + shape.Width * (0.5f + 0.45f * MathF.Sin(t * 0.6f));
        using (var spot = new GraphicsPath())
        {
            spot.AddEllipse(spotX - 90, shape.Y - 50, 180, 90);
            using var spotBrush = new PathGradientBrush(spot) { CenterColor = Palette.WithAlpha(Palette.Bright(), 40), SurroundColors = new[] { Color.Transparent } };
            g.FillPath(spotBrush, spot);
        }
        g.Restore(state);
        // The shining edge
        using (var edgeBrush = Gradient(shape, spin, 235))
        using (var edge = new Pen(edgeBrush, 1.8f + level * 1.2f))
            g.DrawPath(edge, path);

        // Dave's little orb in front of the text
        var orb = new RectangleF(shape.X + PillPadding - 2, shape.Y + Math.Min(shape.Height / 2, 32) - OrbSize / 2, OrbSize, OrbSize);
        if (shape.Width > OrbSize + PillPadding * 2) DrawOrb(g, orb, t * 1.4f, Math.Max(level, 0.15f), Math.Min(1f, contentAlpha * 1.5f));

        DrawText(g, shape);
    }

    private Point BottomCenter(int width, int height)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        return new Point(area.Left + (area.Width - width) / 2, area.Bottom - 40 - height + HaloMargin);
    }

    private void DrawText(Graphics g, RectangleF shape, bool legacy = false)
    {
        var alpha = (int)(255 * contentAlpha);
        if (alpha <= 0) return;
        var left = legacy ? (shape.Width - Measure(text).Width) / 2 : PillPadding + OrbSize + OrbGap; // Legacy: no orb, text centred
        var area = new RectangleF(shape.X + left, shape.Y + 15, shape.Width - left - PillPadding + 4, shape.Height - 30 + 4);
        var (pieces, _) = BubbleText.Layout(g, text, TextFont, EmojiFont, MaxTextWidth); // emoji in their own font, not squares
        var clip = g.Clip;
        g.SetClip(RectangleF.Inflate(area, 2, 4)); // while the pill is still growing, keep the text inside it
        var words = VisibleWords();
        BubbleText.DrawRevealed(g, pieces, Color.Black, alpha / 3, new PointF(area.X + 1, area.Y + 1), words);
        BubbleText.DrawRevealed(g, pieces, Color.White, alpha, area.Location, words);
        g.Clip = clip;
    }

    /// <summary>How many words to show: while Dave says them, the words appear along with his voice; otherwise all.</summary>
    private float VisibleWords()
    {
        if (revealWaiting) return 0.0f;
        if (revealSeconds <= 0) return wordCount + 1;
        var progress = (DateTime.Now - revealStart).TotalSeconds / revealSeconds;
        if (progress >= 1) { revealSeconds = 0; return wordCount + 1; }
        return (float)(progress * wordCount) + 1.2f; // a little ahead of his voice, so it never lags behind
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

/// <summary>
/// The soft purple/cyan glow along the edges of the main monitor. Click-through: it never blocks the mouse.
/// Drawn on its own thread in step with the screen's refresh (at least 60 fps), so it never slows down the bubble or Dave himself.
/// </summary>
public class Glow : LayeredWindow
{
    private const int Detail = 2;          // drawn at half resolution and scaled up: it's soft anyway, and it's 4x less work
    private const double TargetFps = 60;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly object sync = new();
    private Thread? renderer;
    private IntPtr handle;
    private volatile bool disposed;
    private volatile float targetOpacity;
    private float opacity; // only used by the render thread
    private uint[] stretched = Array.Empty<uint>(); // scratch space for ScaleUp, kept between frames

    /// <summary>How loud you're talking (0..1): the glow gets stronger with your voice.</summary>
    public volatile float Level;


    public Glow() : base(clickThrough: true) { }

    public void FadeIn()
    {
        if (!Visible) Show();
        handle = Handle;
        lock (sync)
        {
            targetOpacity = 1;
            if (renderer != null) return;
            renderer = new Thread(RenderLoop) { IsBackground = true, Name = "Glow", Priority = ThreadPriority.AboveNormal };
            renderer.Start();
        }
    }

    public void FadeOut() => targetOpacity = 0;

    private void RenderLoop()
    {
        Surface? small = null, full = null;
        bool failed = false;
        try
        {
            int vsyncs = VsyncsPerFrame();
            double last = Clock.Elapsed.TotalSeconds;
            while (!disposed)
            {
                double now = Clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(0.1, now - last);
                last = now;
                opacity += (targetOpacity - opacity) * (1 - MathF.Exp(-6 * dt));
                lock (sync)
                {
                    if (targetOpacity == 0 && opacity < 0.02f) { opacity = 0; renderer = null; break; }
                }

                var bounds = Screen.PrimaryScreen!.Bounds;
                if (full == null || small == null || full.Width != bounds.Width || full.Height != bounds.Height)
                {
                    small?.Dispose();
                    full?.Dispose();
                    full = new Surface(bounds.Width, bounds.Height);
                    small = new Surface((bounds.Width + Detail - 1) / Detail, (bounds.Height + Detail - 1) / Detail);
                    small.Graphics.ScaleTransform(1f / Detail, 1f / Detail);
                }
                Draw(small.Graphics, new Rectangle(0, 0, bounds.Width, bounds.Height), (float)now, Level);
                ScaleUp(small, full);
                Present(handle, full, bounds.Location, (byte)(Math.Clamp(opacity, 0, 1) * 255));
                WaitForScreen(vsyncs);
            }
        }
        catch (Exception e)
        {
            Log.Write($"Glow stopped: {e.Message}");
            failed = true;
            lock (sync) renderer = null;
        }
        finally
        {
            small?.Dispose();
            full?.Dispose();
        }
        // Hide it on the UI thread, unless it was faded in again in the meantime.
        try { BeginInvoke(() => { if (failed || targetOpacity == 0) Hide(); }); } catch { } // already gone while shutting down
    }

    /// <summary>One frame, in full-screen coordinates. [level] is how loud you're talking (0..1).</summary>
    private static void Draw(Graphics g, Rectangle rect, float t, float level)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

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
    }

    /// <summary>
    /// Smooth 2x scale-up (bilinear) from [small] into [full], spread over all CPU cores. Much faster than GDI+,
    /// which takes longer than a whole frame for this. Each new pixel is 3/4 its own source pixel and 1/4 its neighbour.
    /// </summary>
    private unsafe void ScaleUp(Surface small, Surface full)
    {
        int sw = small.Width, sh = small.Height, fw = full.Width, fh = full.Height;
        if (stretched.Length != fw * sh) stretched = new uint[fw * sh];
        var wide = stretched; // the small rows, already stretched to full width
        uint* src = (uint*)small.Bits, dst = (uint*)full.Bits;

        Parallel.For(0, sh, y =>
        {
            uint* row = src + y * sw;
            fixed (uint* w = &wide[y * fw])
                for (int x = 0; x < fw; x++)
                {
                    int i = x >> 1, n = Math.Clamp((x & 1) == 0 ? i - 1 : i + 1, 0, sw - 1);
                    w[x] = Mix(row[i], row[n]);
                }
        });
        Parallel.For(0, fh, y =>
        {
            int j = Math.Min(y >> 1, sh - 1), n = Math.Clamp((y & 1) == 0 ? j - 1 : j + 1, 0, sh - 1);
            uint* output = dst + y * fw;
            fixed (uint* a = &wide[j * fw], b = &wide[n * fw])
                for (int x = 0; x < fw; x++) output[x] = Mix(a[x], b[x]);
        });
    }

    /// <summary>3/4 of [a] plus 1/4 of [b], for all four colour channels at once.</summary>
    private static uint Mix(uint a, uint b)
    {
        if ((a | b) == 0) return 0; // most of the screen: fully transparent
        uint low = ((a & 0x00FF00FF) * 3 + (b & 0x00FF00FF)) >> 2 & 0x00FF00FF;
        uint high = (((a >> 8) & 0x00FF00FF) * 3 + ((b >> 8) & 0x00FF00FF)) >> 2 & 0x00FF00FF;
        return low | high << 8;
    }

    /// <summary>
    /// How many screen refreshes to wait per frame for about 60 fps: 1 on a 60 Hz screen, 2 on 120-165 Hz, 4 on 240 Hz.
    /// 0 when the screen's timing isn't available.
    /// </summary>
    private static int VsyncsPerFrame()
    {
        var intervals = new List<double>();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 9; i++)
        {
            if (DwmFlush() != 0) return 0;
            if (i > 0) intervals.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
        }
        intervals.Sort();
        double refreshMs = intervals[intervals.Count / 2]; // the middle one: ignores the odd early or late return
        if (refreshMs < 2) return 0; // didn't actually wait for the screen
        // The most refreshes that still give at least ~55 fps; same number every frame, so the motion stays even.
        return Math.Clamp((int)(1000 / (TargetFps - 5) / refreshMs), 1, 8);
    }

    private static void WaitForScreen(int vsyncs)
    {
        if (vsyncs == 0) { Thread.Sleep(15); return; }
        for (int i = 0; i < vsyncs; i++)
            if (DwmFlush() != 0) { Thread.Sleep(15); return; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

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
        disposed = true; // the render thread cleans up after itself
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

    protected void Present(Surface surface, Point location, byte opacity) => Present(Handle, surface, location, opacity);

    /// <summary>Show [surface] in the window [hwnd]; unlike most window calls, this is fine from any thread.</summary>
    protected static void Present(IntPtr hwnd, Surface surface, Point location, byte opacity)
    {
        var position = new NativePoint { X = location.X, Y = location.Y };
        var size = new NativeSize { Width = surface.Width, Height = surface.Height };
        var source = new NativePoint();
        var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 /* per-pixel alpha */ };
        UpdateLayeredWindow(hwnd, IntPtr.Zero, ref position, ref size, surface.DeviceContext, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
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
    /// <summary>The pixels themselves (premultiplied BGRA, top row first), for fast direct access.</summary>
    public IntPtr Bits { get; }
    private readonly Bitmap bitmap;
    private readonly IntPtr dib, previous;

    public Surface(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        var info = new BitmapInfo { Size = 40, Width = Width, Height = -Height /* top-down */, Planes = 1, BitCount = 32 };
        DeviceContext = CreateCompatibleDC(IntPtr.Zero);
        dib = CreateDIBSection(DeviceContext, ref info, 0, out var bits, IntPtr.Zero, 0);
        Bits = bits;
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
