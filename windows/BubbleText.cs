using System.Drawing.Text;
using System.Globalization;

namespace DaveWindows;

/// <summary>
/// Text for the bubble, with emoji. The bubble draws with GDI+, which doesn't switch to the emoji font by itself
/// (🎵 came out as a square), so emoji are drawn with Segoe UI Emoji (white, like the text) and the rest with
/// Segoe UI, with the line wrapping done here.
/// </summary>
public static class BubbleText
{
    public record Piece(string Text, Font Font, PointF At);

    private static readonly StringFormat Exact = CreateFormat();

    private static StringFormat CreateFormat()
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return format;
    }

    /// <summary>Where each piece goes when [text] is wrapped at [maxWidth], and the size of the whole.</summary>
    public static (List<Piece> pieces, SizeF size) Layout(Graphics g, string text, Font font, Font emojiFont, float maxWidth)
    {
        var pieces = new List<Piece>();
        float lineHeight = font.GetHeight(g), x = 0, y = 0, widest = 0;
        var space = g.MeasureString(" ", font, PointF.Empty, Exact).Width;

        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var runs = Runs(word, font, emojiFont).Select(r => (r.text, r.font, width: g.MeasureString(r.text, r.font, PointF.Empty, Exact).Width)).ToList();
                var wordWidth = runs.Sum(r => r.width);
                if (x > 0 && x + space + wordWidth > maxWidth) { x = 0; y += lineHeight; } // doesn't fit: next line
                else if (x > 0) x += space;
                foreach (var (run, runFont, width) in runs)
                {
                    // Emoji are a bit taller in their font; nudge them so they sit on the same line as the text
                    var lift = runFont == emojiFont ? (emojiFont.GetHeight(g) - lineHeight) / 2 : 0;
                    pieces.Add(new Piece(run, runFont, new PointF(x, y - lift)));
                    x += width;
                }
                widest = Math.Max(widest, x);
            }
            x = 0;
            y += lineHeight;
        }
        return (pieces, new SizeF(Math.Min(widest, maxWidth) + 2, Math.Max(y, lineHeight)));
    }

    public static void Draw(Graphics g, List<Piece> pieces, Brush brush, PointF origin)
    {
        foreach (var p in pieces) g.DrawString(p.Text, p.Font, brush, origin.X + p.At.X, origin.Y + p.At.Y, Exact);
    }

    /// <summary>A word split into pieces of normal text and emoji (an emoji with its variation selectors and joiners stays together).</summary>
    private static IEnumerable<(string text, Font font)> Runs(string word, Font font, Font emojiFont)
    {
        var current = new System.Text.StringBuilder();
        bool? currentIsEmoji = null;
        var elements = StringInfo.GetTextElementEnumerator(word);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var emoji = IsEmoji(element);
            if (currentIsEmoji != null && emoji != currentIsEmoji)
            {
                yield return (current.ToString(), currentIsEmoji.Value ? emojiFont : font);
                current.Clear();
            }
            current.Append(element);
            currentIsEmoji = emoji;
        }
        if (current.Length > 0) yield return (current.ToString(), currentIsEmoji == true ? emojiFont : font);
    }

    private static bool IsEmoji(string element)
    {
        int c;
        try { c = char.ConvertToUtf32(element, 0); } catch { return false; } // a broken surrogate: just draw it as text
        if (c >= 0x1F000) return true;                                   // most emoji (🎵 📸 👀 …)
        if (element.Contains('️') || element.Contains('‍')) return true; // shown as emoji on purpose
        return c is >= 0x2190 and <= 0x21FF or >= 0x2300 and <= 0x23FF or >= 0x2460 and <= 0x24FF
               or >= 0x25A0 and <= 0x27BF or >= 0x2900 and <= 0x297F or >= 0x2B00 and <= 0x2BFF; // ⏰ ⌨ ✅ ❤ ➕ ⏭ …
    }
}
