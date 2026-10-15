using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DaveWindows;

/// <summary>
/// A friend's message as a small glass card in the top-left corner of the screen (instead of in the bubble): who it's from,
/// the message, and a little picture for a photo. Newest on top, at most three; each stays long enough to read (longer while
/// the mouse is on it) and then slides away. Click one to open that chat in Dave's window. Never takes the focus. With
/// "Keep Dave off games" on, never over a full-screen game or Roblox (see GameGuard).
/// </summary>
public sealed class MessageCards : LayeredWindow
{
    private const int CardWidth = 330, CardHeight = 74, Gap = 10, Inset = 20, Room = 18, MaxCards = 3;
    private static readonly Font NameFont = new("Segoe UI Semibold", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font TextFont = new("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font SmallFont = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font InitialFont = new("Segoe UI Semibold", 18f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>A card was clicked: the friend's code.</summary>
    public event Action<string>? Clicked;

    private readonly System.Windows.Forms.Timer frames = new() { Interval = 33 }; // ~30 fps, only while a card is showing
    private readonly List<Card> cards = new();
    private Surface? surface;
    private float shown;
    private Screen? place;
    private Point at;

    private sealed class Card
    {
        public string Code = "", Name = "", Text = "";
        public Image? Picture;     // a photo they sent, small
        public double Stay;        // seconds left on screen (counts down while the mouse isn't on it)
        public float Appear;       // 0..1 sliding in, back to 0 sliding out
        public bool Leaving;
        public float Y;            // where it is in the stack (moves smoothly when one is added or goes)
    }

    public MessageCards() : base(clickThrough: false)
    {
        frames.Tick += (_, _) => Frame();
        MouseClick += (_, e) => { if (CardAt(e.Location) is { } card) { Clicked?.Invoke(card.Code); card.Leaving = true; } };
    }

    /// <summary>Shows a friend's message. [text] is what it says ("sent a photo" for a file); [picture] a photo to show small.</summary>
    public void Add(string code, string name, string text, string picture = "")
    {
        if (InvokeRequired) { BeginInvoke(() => Add(code, name, text, picture)); return; }
        Image? small = null;
        try { if (picture.Length > 0 && File.Exists(picture)) using (var full = Image.FromFile(picture)) small = Thumbnail(full, 52); }
        catch { /* not a picture we can show */ }
        cards.Insert(0, new Card { Code = code, Name = name, Text = text, Picture = small, Stay = Math.Clamp(4 + text.Length / 18.0, 5, 12), Y = -1 });
        foreach (var extra in cards.Skip(MaxCards)) extra.Leaving = true;
        if (!frames.Enabled) frames.Start();
    }

    private Card? CardAt(Point p)
    {
        foreach (var c in cards)
        {
            var r = new RectangleF(Room, Room + c.Y * (CardHeight + Gap), CardWidth, CardHeight);
            if (!c.Leaving && r.Contains(p)) return c;
        }
        return null;
    }

    private void Frame()
    {
        // The mouse on a card keeps it there
        var mouse = Visible ? PointToClient(Cursor.Position) : new Point(-1, -1);
        var hovered = CardAt(mouse);
        foreach (var c in cards)
        {
            if (c != hovered) c.Stay -= frames.Interval / 1000.0;
            if (c.Stay <= 0) c.Leaving = true;
            c.Appear = c.Leaving ? Math.Max(0, c.Appear - 0.08f) : Math.Min(1, c.Appear + 0.07f);
        }
        foreach (var gone in cards.Where(c => c.Leaving && c.Appear <= 0).ToList()) { gone.Picture?.Dispose(); cards.Remove(gone); }
        int slot = 0;
        foreach (var c in cards)
        {
            float target = slot;
            c.Y = c.Y < 0 ? target : c.Y + (target - c.Y) * 0.25f;
            if (!c.Leaving) slot++;
        }
        if (cards.Count == 0)
        {
            frames.Stop();
            Hide();
            return;
        }

        var target2 = GameGuard.ScreenForDave();
        bool move = target2?.DeviceName != place?.DeviceName;
        shown += ((target2 == null || move ? 0 : 1) - shown) * 0.2f;
        if (move && shown < 0.05f) place = target2;
        if (place == null) { if (Visible && surface != null) Present(surface, at, 0); return; }

        int width = CardWidth + 2 * Room, height = (MaxCards + 1) * (CardHeight + Gap) + 2 * Room;
        if (surface == null || surface.Width != width || surface.Height != height) { surface?.Dispose(); surface = new Surface(width, height); }
        var g = surface.Graphics;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        double t = Clock.Elapsed.TotalSeconds;
        foreach (var c in cards) DrawCard(g, c, new RectangleF(Room, Room + c.Y * (CardHeight + Gap), CardWidth, CardHeight), t, c == hovered);

        if (!Visible) Show();
        var area = place.WorkingArea;
        at = new Point(area.Left + Inset - Room, area.Top + Inset - Room);
        Present(surface, at, (byte)(Math.Clamp(shown, 0, 1) * 255));
    }

    private static void DrawCard(Graphics g, Card card, RectangleF rect, double t, bool hovered)
    {
        float appear = Ease(card.Appear);
        if (appear <= 0.01f) return;
        rect.Offset(-(1 - appear) * 50, 0); // slides in from the left
        int A(float a) => (int)Math.Clamp(a * appear, 0, 255);
        float spin = (float)(t * 40 % 360);

        // A soft glow, a bit stronger while the mouse is on it
        for (int i = 3; i >= 1; i--)
        {
            var halo = RectangleF.Inflate(rect, i * 4, i * 4);
            using var haloPath = Rounded(halo, 18 + i * 4);
            using var haloBrush = EdgeBrush(halo, spin, A((hovered ? 26 : 14) / (float)i * 2));
            g.FillPath(haloBrush, haloPath);
        }
        using var path = Rounded(rect, 18);
        using (var glass = new SolidBrush(Palette.WithAlpha(Palette.Deep, A(238)))) g.FillPath(glass, path);
        using (var tint = EdgeBrush(rect, 20, A(hovered ? 70 : 50))) g.FillPath(tint, path);
        using (var edgeBrush = EdgeBrush(rect, spin, A(210)))
        using (var edge = new Pen(edgeBrush, 1.5f)) g.DrawPath(edge, path);

        // Their initial in a round, coloured badge
        var badge = new RectangleF(rect.X + 14, rect.Y + (rect.Height - 40) / 2, 40, 40);
        using (var fill = new LinearGradientBrush(badge, Palette.WithAlpha(Palette.Purple, A(255)), Palette.WithAlpha(Palette.Cyan, A(255)), 45f)) g.FillEllipse(fill, badge);
        using (var white = new SolidBrush(Color.FromArgb(A(255), 255, 255, 255)))
        using (var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString((card.Name.Trim().FirstOrDefault() is var first and not '\0' ? char.ToUpper(first) : '?').ToString(), InitialFont, white, badge, centred);

        // A photo they sent, small, on the right
        float right = rect.Right - 14;
        if (card.Picture != null)
        {
            var pic = new RectangleF(rect.Right - 14 - 46, rect.Y + (rect.Height - 46) / 2, 46, 46);
            using var clip = Rounded(pic, 9);
            var state = g.Save();
            g.SetClip(clip);
            using (var faded = new System.Drawing.Imaging.ImageAttributes())
            {
                faded.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = appear });
                g.DrawImage(card.Picture, Rectangle.Round(pic), 0, 0, card.Picture.Width, card.Picture.Height, GraphicsUnit.Pixel, faded);
            }
            g.Restore(state);
            right = pic.X - 10;
        }

        // Who, and what they said (two lines at most)
        float textX = badge.Right + 12, textWidth = right - textX;
        using (var nameBrush = new SolidBrush(Color.FromArgb(A(255), 255, 255, 255)))
        using (var one = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
            g.DrawString(card.Name, NameFont, nameBrush, new RectangleF(textX, rect.Y + 10, textWidth, 20), one);
        // (with BubbleText, so emoji show as emoji instead of squares; two lines at most, then "…")
        var (pieces, _) = BubbleText.Layout(g, card.Text, TextFont, EmojiFont, textWidth);
        float line = TextFont.GetHeight(g);
        var kept = pieces.Where(p => p.At.Y < line * 1.5f).ToList();
        if (kept.Count < pieces.Count && kept.Count > 0)
        {
            var last = kept[^1];
            kept[^1] = last with { Text = last.Font == EmojiFont ? "…" : last.Text.Length > 3 ? last.Text[..^2] + "…" : "…", Font = TextFont };
        }
        using (var textBrush = new SolidBrush(Color.FromArgb(A(205), 255, 255, 255))) BubbleText.Draw(g, kept, textBrush, new PointF(textX, rect.Y + 31));
    }

    private static readonly Font EmojiFont = new("Segoe UI Emoji", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static Image Thumbnail(Image full, int size)
    {
        // a square from the middle, made small
        int side = Math.Min(full.Width, full.Height);
        var small = new Bitmap(size, size);
        using var g = Graphics.FromImage(small);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(full, new Rectangle(0, 0, size, size), new Rectangle((full.Width - side) / 2, (full.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        return small;
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

    /// <summary>The cards as they are now, drawn into a picture (for checking how they look, without showing anything).</summary>
    public Bitmap Snapshot()
    {
        foreach (var c in cards) { c.Appear = 1; c.Y = cards.IndexOf(c); }
        var picture = new Bitmap(CardWidth + 2 * Room, cards.Count * (CardHeight + Gap) + 2 * Room);
        using var g = Graphics.FromImage(picture);
        g.Clear(Color.FromArgb(30, 30, 40));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        foreach (var c in cards) DrawCard(g, c, new RectangleF(Room, Room + c.Y * (CardHeight + Gap), CardWidth, CardHeight), 0, false);
        frames.Stop();
        return picture;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { frames.Dispose(); surface?.Dispose(); foreach (var c in cards) c.Picture?.Dispose(); }
        base.Dispose(disposing);
    }
}
