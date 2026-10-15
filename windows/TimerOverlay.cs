using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DaveWindows;

/// <summary>
/// Timers on the screen ("pizza in 12 minutes"): small glass cards in the top-right corner, in the theme's colours, with a
/// ring that runs down and the time left. When one is done it glows and fades away (Dave says it as always). Click-through,
/// never takes focus. The stopwatch (see StopwatchClock) is a card on top, counting up. With "Keep Dave off games" on, never over a full-screen game or Roblox (see GameGuard). Off in the settings.
/// </summary>
public sealed class TimerOverlay : LayeredWindow
{
    private const int CardWidth = 250, CardHeight = 60, Gap = 10, Inset = 20, Room = 18, MaxCards = 3;
    private static readonly TimeSpan Longest = TimeSpan.FromHours(3);      // longer ones are reminders, not timers
    private static readonly Font TimeFont = new("Segoe UI Semibold", 22f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font LabelFont = new("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Settings settings;
    private readonly System.Windows.Forms.Timer frames = new() { Interval = 33 }; // ~30 fps, only while a card is showing
    private readonly List<Card> cards = new();
    private Surface? surface;
    private float shown;  // 0..1: faded out while moving to another monitor, or while there's no free one
    private Screen? place; // the monitor the cards are on
    private Point at;      // where the window is

    private sealed class Card
    {
        public long Id;
        public string Message = "";
        public DateTimeOffset Start, At;
        public float Appear;          // 0..1, sliding in
        public double? DoneAt;        // when it went off (or was cancelled), in Clock seconds
        public bool Cancelled;
        public bool Stopwatch;        // the stopwatch's card, not a timer's
        public TimeSpan Final;        // the stopwatch: its time when it was stopped
    }

    public TimerOverlay(Settings settings) : base(clickThrough: true)
    {
        this.settings = settings;
        frames.Tick += (_, _) => Frame();
    }

    /// <summary>Every second (and after a reminder changes): which timers to show.</summary>
    public void Sync()
    {
        var now = DateTimeOffset.Now;
        bool watchShown = settings.TimersOnScreen && settings.Stopwatch.On;
        var timers = settings.TimersOnScreen
            ? settings.Reminders.Where(r => r.Repeat.Length == 0 && r.At > now && r.At - StartOf(r) <= Longest).OrderBy(r => r.At).Take(MaxCards - (watchShown ? 1 : 0)).ToList()
            : new List<Settings.Reminder>();

        // The stopwatch: on top while it's on; when it stops it shows its time for a moment, then fades
        var watch = cards.FirstOrDefault(c => c.Stopwatch && c.DoneAt == null);
        if (watchShown && watch == null) cards.Insert(0, new Card { Id = -1, Stopwatch = true });
        else if (!watchShown && watch != null)
        {
            watch.DoneAt = Clock.Elapsed.TotalSeconds;
            watch.Cancelled = !settings.TimersOnScreen;
            watch.Final = StopwatchClock.Elapsed(settings);
        }

        foreach (var r in timers)
        {
            var card = cards.FirstOrDefault(c => c.Id == r.Id);
            if (card == null) cards.Add(new Card { Id = r.Id, Message = r.Message, Start = StartOf(r), At = r.At });
            else { card.Message = r.Message; card.At = r.At; }
        }
        foreach (var card in cards.Where(c => !c.Stopwatch && c.DoneAt == null && timers.All(r => r.Id != c.Id)))
        {
            // Gone: went off (it's time), or was cancelled / the cards were turned off (fade away quietly)
            card.DoneAt = Clock.Elapsed.TotalSeconds;
            card.Cancelled = !settings.TimersOnScreen || now < card.At.AddSeconds(-1.5);
        }
        if (cards.Count > 0 && !frames.Enabled) frames.Start();
    }

    /// <summary>When a timer was set: its id is the moment it was made.</summary>
    private static DateTimeOffset StartOf(Settings.Reminder r)
    {
        try
        {
            var made = new DateTimeOffset(new DateTime(r.Id, DateTimeKind.Local));
            if (made < r.At && r.At - made < TimeSpan.FromDays(2)) return made;
        }
        catch { /* not a time */ }
        return r.At - TimeSpan.FromMinutes(10);
    }

    private void Frame()
    {
        double t = Clock.Elapsed.TotalSeconds;
        cards.RemoveAll(c => c.DoneAt is { } done && t - done > (c.Cancelled ? 0.5 : 4.5));
        if (cards.Count == 0)
        {
            frames.Stop();
            Hide();
            return;
        }

        // On Dave's monitor (with "Keep Dave off games": another one while a game is in front, or none). Moving to
        // another monitor fades out first and comes back in there.
        var target = GameGuard.ScreenForDave();
        bool move = target?.DeviceName != place?.DeviceName;
        shown += ((target == null || move ? 0 : 1) - shown) * 0.2f;
        if (move && shown < 0.05f) place = target;
        foreach (var c in cards) c.Appear = Math.Min(1, c.Appear + 0.07f);
        if (place == null) { if (Visible && surface != null) Present(surface, at, 0); return; } // nowhere free: out of sight

        var area = place.WorkingArea;
        int width = CardWidth + 2 * Room, height = Math.Max(MaxCards, cards.Count) * (CardHeight + Gap) + 2 * Room;
        if (surface == null || surface.Width != width || surface.Height != height) { surface?.Dispose(); surface = new Surface(width, height); }
        var g = surface.Graphics;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        for (int i = 0; i < cards.Count; i++) DrawCard(g, cards[i], new RectangleF(Room, Room + i * (CardHeight + Gap), CardWidth, CardHeight), t);

        if (!Visible) Show();
        at = new Point(area.Right - Inset - CardWidth - Room, area.Top + Inset - Room);
        Present(surface, at, (byte)(Math.Clamp(shown, 0, 1) * 255));
    }

    private void DrawCard(Graphics g, Card card, RectangleF rect, double t)
    {
        if (card.Stopwatch) { DrawStopwatch(g, card, rect, t); return; }
        var now = DateTimeOffset.Now;
        float appear = Ease(card.Appear);
        float fade = card.DoneAt is { } doneAt ? (float)Math.Clamp(1 - (t - doneAt - (card.Cancelled ? 0 : 3.8)) / (card.Cancelled ? 0.5 : 0.7), 0, 1) : 1;
        float alpha = appear * fade;
        if (alpha <= 0.01f) return;
        rect.Offset((1 - appear) * 40, 0); // slides in from the right

        var left = card.At - now;
        bool done = !card.Cancelled && (card.DoneAt != null || left.TotalSeconds <= 0); // the moment it runs out
        double total = Math.Max(1, (card.At - card.Start).TotalSeconds);
        float remaining = done ? 0 : (float)Math.Clamp(left.TotalSeconds / total, 0, 1);
        bool ending = !done && left.TotalSeconds <= 10;
        float pulse = done ? 0.5f + 0.5f * MathF.Sin((float)t * 9) : ending ? 0.5f + 0.5f * MathF.Sin((float)t * 6) : 0;
        int A(float a) => (int)Math.Clamp(a * alpha, 0, 255);
        float spin = (float)(t * 40 % 360);

        // A soft glow around it, stronger at the end and when it's done
        for (int i = 3; i >= 1; i--)
        {
            var halo = RectangleF.Inflate(rect, i * 4 + pulse * 6, i * 4 + pulse * 6);
            using var haloPath = Rounded(halo, 18 + i * 4);
            using var haloBrush = EdgeBrush(halo, spin, A((14 + pulse * 40) / i * 2));
            g.FillPath(haloBrush, haloPath);
        }

        using var path = Rounded(rect, 18);
        using (var glass = new SolidBrush(Palette.WithAlpha(Palette.Deep, A(236)))) g.FillPath(glass, path);
        using (var tint = EdgeBrush(rect, 20, A(50))) g.FillPath(tint, path);
        using (var edgeBrush = EdgeBrush(rect, spin, A(210 + pulse * 45)))
        using (var edge = new Pen(edgeBrush, 1.5f + pulse)) g.DrawPath(edge, path);

        // The ring that runs down, with a glowing dot where it ends
        var centre = new PointF(rect.X + 31, rect.Y + rect.Height / 2);
        const float R = 18;
        var ringRect = new RectangleF(centre.X - R, centre.Y - R, R * 2, R * 2);
        using (var track = new Pen(Color.FromArgb(A(40), 255, 255, 255), 4)) g.DrawEllipse(track, ringRect);
        float sweep = done ? 360 : 360 * remaining;
        if (sweep > 0.5f)
        {
            using var ringBrush = new LinearGradientBrush(RectangleF.Inflate(ringRect, 2, 2), Palette.WithAlpha(Palette.Cyan, A(255)), Palette.WithAlpha(Palette.Purple, A(255)), 45f);
            using var ring = new Pen(ringBrush, 4 + pulse * 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(ring, ringRect, -90, -sweep);
            if (!done)
            {
                float a = (-90 - sweep) * MathF.PI / 180;
                var tip = new PointF(centre.X + MathF.Cos(a) * R, centre.Y + MathF.Sin(a) * R);
                using var dot = new GraphicsPath();
                dot.AddEllipse(tip.X - 7, tip.Y - 7, 14, 14);
                using var dotBrush = new PathGradientBrush(dot) { CenterColor = Color.FromArgb(A(230), 255, 255, 255), SurroundColors = new[] { Color.Transparent } };
                g.FillPath(dotBrush, dot);
            }
        }
        if (done)
        {
            // a tick in the middle
            using var tick = new Pen(Color.FromArgb(A(255), 255, 255, 255), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(tick, new[] { new PointF(centre.X - 7, centre.Y), new PointF(centre.X - 2, centre.Y + 5), new PointF(centre.X + 8, centre.Y - 6) });
        }

        // The time left and what it's for
        float textX = rect.X + 62, textWidth = rect.Right - 14 - textX;
        var big = done ? settings.Say("Done!", "Klaar!") : Countdown(left);
        using (var white = new SolidBrush(Color.FromArgb(A(255), 255, 255, 255))) g.DrawString(big, TimeFont, white, textX, rect.Y + 6);
        using var label = new SolidBrush(Color.FromArgb(A(175), 255, 255, 255));
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(card.Message, LabelFont, label, new RectangleF(textX, rect.Y + 35, textWidth, 18), format);
    }

    /// <summary>The stopwatch's card: the time counting up (with tenths), a ring going round once a minute, and the laps.</summary>
    private void DrawStopwatch(Graphics g, Card card, RectangleF rect, double t)
    {
        float appear = Ease(card.Appear);
        float fade = card.DoneAt is { } doneAt ? (float)Math.Clamp(1 - (t - doneAt - (card.Cancelled ? 0 : 3.8)) / (card.Cancelled ? 0.5 : 0.7), 0, 1) : 1;
        float alpha = appear * fade;
        if (alpha <= 0.01f) return;
        rect.Offset((1 - appear) * 40, 0);
        int A(float a) => (int)Math.Clamp(a * alpha, 0, 255);

        var w = settings.Stopwatch;
        bool stopped = card.DoneAt != null, paused = !stopped && w.RunningSince == null;
        var elapsed = stopped ? card.Final : StopwatchClock.Elapsed(settings);
        float breathe = paused ? 0.55f + 0.45f * MathF.Sin((float)t * 3) : 1; // paused: the time breathes
        float spin = (float)(t * 40 % 360);

        for (int i = 3; i >= 1; i--)
        {
            var halo = RectangleF.Inflate(rect, i * 4, i * 4);
            using var haloPath = Rounded(halo, 18 + i * 4);
            using var haloBrush = EdgeBrush(halo, spin, A(14f / i * 2));
            g.FillPath(haloBrush, haloPath);
        }
        using var path = Rounded(rect, 18);
        using (var glass = new SolidBrush(Palette.WithAlpha(Palette.Deep, A(236)))) g.FillPath(glass, path);
        using (var tint = EdgeBrush(rect, 20, A(50))) g.FillPath(tint, path);
        using (var edgeBrush = EdgeBrush(rect, spin, A(210)))
        using (var edge = new Pen(edgeBrush, 1.5f)) g.DrawPath(edge, path);

        // The ring fills up once a minute, like a second hand
        var centre = new PointF(rect.X + 31, rect.Y + rect.Height / 2);
        const float R = 18;
        var ringRect = new RectangleF(centre.X - R, centre.Y - R, R * 2, R * 2);
        using (var track = new Pen(Color.FromArgb(A(40), 255, 255, 255), 4)) g.DrawEllipse(track, ringRect);
        float sweep = stopped ? 360 : (float)(elapsed.TotalSeconds % 60 / 60 * 360);
        if (sweep > 0.5f)
        {
            using var ringBrush = new LinearGradientBrush(RectangleF.Inflate(ringRect, 2, 2), Palette.WithAlpha(Palette.Cyan, A(255 * breathe)), Palette.WithAlpha(Palette.Purple, A(255 * breathe)), 45f);
            using var ring = new Pen(ringBrush, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(ring, ringRect, -90, sweep);
            if (!stopped)
            {
                float a = (-90 + sweep) * MathF.PI / 180;
                var tip = new PointF(centre.X + MathF.Cos(a) * R, centre.Y + MathF.Sin(a) * R);
                using var dot = new GraphicsPath();
                dot.AddEllipse(tip.X - 7, tip.Y - 7, 14, 14);
                using var dotBrush = new PathGradientBrush(dot) { CenterColor = Color.FromArgb(A(230 * breathe), 255, 255, 255), SurroundColors = new[] { Color.Transparent } };
                g.FillPath(dotBrush, dot);
            }
        }
        // in the middle: two bars while paused, a square when stopped
        using (var mark = new SolidBrush(Color.FromArgb(A(235), 255, 255, 255)))
        {
            if (paused) { g.FillRectangle(mark, centre.X - 5, centre.Y - 6, 3.5f, 12); g.FillRectangle(mark, centre.X + 1.5f, centre.Y - 6, 3.5f, 12); }
            else if (stopped) g.FillRectangle(mark, centre.X - 5, centre.Y - 5, 10, 10);
        }

        float textX = rect.X + 62, textWidth = rect.Right - 14 - textX;
        using (var white = new SolidBrush(Color.FromArgb(A(255 * breathe), 255, 255, 255))) g.DrawString(StopwatchClock.Short(elapsed), TimeFont, white, textX, rect.Y + 6);
        var laps = w.Laps;
        var label = stopped ? settings.Say("Stopwatch · stopped", "Stopwatch · gestopt")
            : paused ? settings.Say("Stopwatch · paused", "Stopwatch · gepauzeerd")
            : settings.Say("Stopwatch", "Stopwatch");
        if (laps.Count > 0)
        {
            var last = laps[^1] - (laps.Count > 1 ? laps[^2] : 0);
            label += settings.Say($" · lap {laps.Count}: ", $" · ronde {laps.Count}: ") + StopwatchClock.Short(TimeSpan.FromSeconds(last));
        }
        using var labelBrush = new SolidBrush(Color.FromArgb(A(175), 255, 255, 255));
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(label, LabelFont, labelBrush, new RectangleF(textX, rect.Y + 35, textWidth, 18), format);
    }

    /// <summary>Draws the cards as they are now into a picture (for checking how they look, without showing anything).</summary>
    public Bitmap Snapshot()
    {
        Sync();
        foreach (var c in cards) c.Appear = 1;
        int width = CardWidth + 2 * Room, height = Math.Max(MaxCards, cards.Count) * (CardHeight + Gap) + 2 * Room;
        var picture = new Bitmap(width, height);
        using var g = Graphics.FromImage(picture);
        g.Clear(Color.FromArgb(30, 30, 40));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        for (int i = 0; i < cards.Count; i++) DrawCard(g, cards[i], new RectangleF(Room, Room + i * (CardHeight + Gap), CardWidth, CardHeight), Clock.Elapsed.TotalSeconds);
        frames.Stop();
        return picture;
    }

    /// <summary>"4:59", "12:03", "1:05:12".</summary>
    private static string Countdown(TimeSpan left)
    {
        var s = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private static float Ease(float x) => 1 - (1 - x) * (1 - x) * (1 - x);

    private static LinearGradientBrush EdgeBrush(RectangleF rect, float angle, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        return new LinearGradientBrush(RectangleF.Inflate(rect, 1, 1), Color.Black, Color.Black, angle)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[] { Palette.WithAlpha(Palette.Purple, alpha), Palette.WithAlpha(Palette.Cyan, alpha), Palette.WithAlpha(Palette.Pink, alpha) },
                Positions = new[] { 0f, 0.5f, 1f },
            },
        };
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { frames.Dispose(); surface?.Dispose(); }
        base.Dispose(disposing);
    }
}
