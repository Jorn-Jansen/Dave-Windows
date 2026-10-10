using System.Drawing.Drawing2D;

namespace DaveWindows;

/// <summary>
/// Each theme's own effect around the bubble: northern lights rising from it (Aurora), flames and embers (Ember), slime
/// dripping off it (Toxic), a water surface (Ocean), cherry blossom petals (Sakura), shooting stars (Midnight). Stronger
/// while you or Dave talk. Legacy has none.
/// </summary>
public partial class Bubble
{
    private bool IsCircle => listening && size.Width < CircleSize * 1.4f;

    /// <summary>Points along the top of the shape (the pill's top edge, or the orb's upper arc), with their outward direction and 0..1 along it.</summary>
    private List<(PointF p, PointF n, float u)> TopEdge(RectangleF shape, float radius, float step)
    {
        var edge = new List<(PointF, PointF, float)>();
        if (IsCircle)
        {
            float cx = shape.X + shape.Width / 2, cy = shape.Y + shape.Width / 2, r = shape.Width / 2;
            int n = Math.Max(8, (int)(MathF.PI * r * 0.84f / step));
            for (int i = 0; i <= n; i++)
            {
                float a = MathF.PI * (1.08f + 0.84f * i / n), u = i / (float)n;
                var d = new PointF(MathF.Cos(a), MathF.Sin(a));
                edge.Add((new PointF(cx + d.X * r, cy + d.Y * r), d, u));
            }
        }
        else
        {
            // Around the left round end, along the top, and around the right round end: down to halfway the sides
            float r = Math.Min(radius, shape.Height / 2), arc = MathF.PI / 2 * r, straight = Math.Max(0, shape.Width - 2 * r);
            float total = 2 * arc + straight;
            int n = Math.Max(8, (int)(total / step));
            var left = new PointF(shape.X + r, shape.Y + r);
            var right = new PointF(shape.Right - r, shape.Y + r);
            for (int i = 0; i <= n; i++)
            {
                float s = total * i / n;
                PointF p, d;
                if (s < arc) { float a = MathF.PI + s / arc * MathF.PI / 2; d = new PointF(MathF.Cos(a), MathF.Sin(a)); p = new PointF(left.X + d.X * (r - 1), left.Y + d.Y * (r - 1)); }
                else if (s <= arc + straight) { d = new PointF(0, -1); p = new PointF(left.X + (s - arc), shape.Y + 1); }
                else { float a = MathF.PI * 1.5f + (s - arc - straight) / arc * MathF.PI / 2; d = new PointF(MathF.Cos(a), MathF.Sin(a)); p = new PointF(right.X + d.X * (r - 1), right.Y + d.Y * (r - 1)); }
                edge.Add((p, d, i / (float)n));
            }
        }
        return edge;
    }

    /// <summary>How far an effect reaches at this spot along the edge: fully in the middle, smoothly down to nothing at both ends.</summary>
    private float Taper(float u, RectangleF shape, float radius)
    {
        float zone = IsCircle ? 0.3f : Math.Clamp(Math.Min(radius, shape.Height / 2) * 2.2f / (shape.Width + 1), 0.05f, 0.45f);
        float x = Math.Clamp(Math.Min(u, 1 - u) / zone, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>A point along the bottom of the shape (where slime drips off), relative to the shape's centre.</summary>
    private PointF BottomPoint(float u)
    {
        if (IsCircle)
        {
            float a = MathF.PI * (0.18f + 0.64f * u), r = size.Width / 2 - 1;
            return new PointF(MathF.Cos(a) * r, MathF.Sin(a) * r);
        }
        float radius = Math.Min(size.Height / 2f, 34f);
        return new PointF((u - 0.5f) * (size.Width - radius * 1.6f), size.Height / 2 - 1);
    }

    private float Rnd(float from, float to) => from + (float)random.NextDouble() * (to - from);

    private static float Smooth(float x) => 0.5f + 0.5f * MathF.Sin(x);

    // --- Behind the shape: drawn every frame from the time, no particles ---

    private void DrawThemeBehind(Graphics g, RectangleF shape, float radius, float voice, float t)
    {
        if (contentAlpha <= 0.01f && !IsCircle) return;
        float strength = Math.Clamp(0.55f + voice * 1.2f, 0, 1.6f);
        switch (Palette.Id)
        {
            case "aurora":
            {
                // Curtains of light rising from the top, waving and shifting colour
                foreach (var (p, n, u) in TopEdge(shape, radius, 4))
                {
                    float fade = Taper(u, shape, radius);
                    float height = (30 + voice * 46) * (0.35f + 0.65f * Smooth(u * 7 + t * 0.7f) * Smooth(u * 17 - t * 1.3f + 1)) * strength * fade;
                    float sway = IsCircle ? 0 : 5 * MathF.Sin(t * 1.1f + u * 9) * fade * -n.Y; // only on the flat top, so the ends hug the curve
                    var end = new PointF(p.X + n.X * height + sway, p.Y + n.Y * height);
                    if (height < 2) continue;
                    using var brush = new LinearGradientBrush(p, end, Palette.WithAlpha(Along(u * 0.7f + t * 0.04f), (int)(210 * contentAlphaOrOne())), Color.Transparent);
                    using var pen = new Pen(brush, 5.5f);
                    g.DrawLine(pen, p, end);
                }
                break;
            }
            case "ocean":
            {
                // A water surface along the top, with waves running along it and foam on the crest
                var edge = TopEdge(shape, radius, 4);
                var crest = edge.Select(e =>
                {
                    float h = (11 + 5 * MathF.Sin(e.u * 14 - t * 3) + 3 * MathF.Sin(e.u * 31 + t * 2.2f) + voice * 12) * strength * Taper(e.u, shape, radius);
                    return new PointF(e.p.X + e.n.X * h, e.p.Y + e.n.Y * h);
                }).ToArray();
                using var water = new GraphicsPath();
                water.AddLines(edge.Select(e => e.p).ToArray());
                water.AddLines(crest.Reverse().ToArray());
                water.CloseFigure();
                var bounds = water.GetBounds();
                using (var fill = new LinearGradientBrush(RectangleF.Inflate(bounds, 1, 1), Palette.WithAlpha(Palette.Cyan, 190), Palette.WithAlpha(Palette.Purple, 70), 90f))
                    g.FillPath(fill, water);
                if (crest.Length > 1)
                    using (var foam = new Pen(Color.FromArgb((int)(170 * contentAlphaOrOne()), 235, 250, 255), 2.2f)) g.DrawCurve(foam, crest, 0.5f);
                break;
            }
            case "toxic":
            {
                // Slime hanging along the bottom edge, slowly swelling, where the drops come from
                float cx = shape.X + shape.Width / 2, cy = shape.Y + shape.Height / 2;
                for (int i = 0; i < 7; i++)
                {
                    var at = BottomPoint((i + 0.5f) / 7);
                    float r = (3 + 3.5f * Smooth(t * 1.3f + i * 1.7f)) * (0.8f + voice);
                    float x = cx + at.X, y = cy + at.Y;
                    using (var glowBrush = new SolidBrush(Palette.WithAlpha(Palette.Pink, (int)(60 * contentAlphaOrOne())))) g.FillEllipse(glowBrush, x - r * 2, y - r, r * 4, r * 3.2f);
                    using (var goo = new SolidBrush(Palette.WithAlpha(Palette.Pink, (int)(225 * contentAlphaOrOne())))) g.FillEllipse(goo, x - r * 1.3f, y - r * 0.6f, r * 2.6f, r * 2.1f);
                }
                break;
            }
            case "ember":
            {
                // Flames licking up from the top, flickering
                var edge = TopEdge(shape, radius, 4);
                for (int k = 0; k < edge.Count; k += 2)
                {
                    var (p, n, u) = edge[k];
                    float fade = Taper(u, shape, radius);
                    if (fade < 0.04f) continue;
                    var tangent = new PointF(-n.Y, n.X);
                    float flicker = Smooth(t * 9 + k * 1.7f) * 0.5f + Smooth(t * 13.7f + k * 0.9f) * 0.5f;
                    float height = ((18 + voice * 32) * (0.45f + 0.55f * flicker) * strength + 5) * fade;
                    float width = 14 * (0.4f + 0.6f * fade), lean = 4 * MathF.Sin(t * 6 + k) * fade;
                    var tip = new PointF(p.X + n.X * height + tangent.X * lean, p.Y + n.Y * height + tangent.Y * lean);
                    var left = new PointF(p.X - tangent.X * width / 2, p.Y - tangent.Y * width / 2);
                    var right = new PointF(p.X + tangent.X * width / 2, p.Y + tangent.Y * width / 2);
                    using var flame = new GraphicsPath();
                    flame.AddBezier(left, new PointF(left.X + n.X * height * 0.55f, left.Y + n.Y * height * 0.55f), new PointF(tip.X - tangent.X * 2, tip.Y - tangent.Y * 2), tip);
                    flame.AddBezier(tip, new PointF(tip.X + tangent.X * 2, tip.Y + tangent.Y * 2), new PointF(right.X + n.X * height * 0.55f, right.Y + n.Y * height * 0.55f), right);
                    flame.CloseFigure();
                    using var brush = new LinearGradientBrush(RectangleF.Inflate(flame.GetBounds(), 1, 1), Color.Black, Color.Black, 0f);
                    brush.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Palette.WithAlpha(Palette.Purple, 0), Palette.WithAlpha(Palette.Pink, 200), Color.FromArgb(235, 255, 236, 170) },
                        Positions = new[] { 0f, 0.45f, 1f },
                    };
                    var from = tip; var to = p; // bright at the base, fading towards the tip
                    brush.ResetTransform();
                    using var along = new LinearGradientBrush(from, to, Color.Transparent, Color.Transparent) { InterpolationColors = brush.InterpolationColors };
                    g.FillPath(along, flame);
                }
                break;
            }
        }
    }

    /// <summary>Effects show from the moment the shape appears (the orb) or once its text is in (the pill).</summary>
    private float contentAlphaOrOne() => IsCircle ? 1 : contentAlpha;

    // --- In front of the shape: particles ---

    /// <summary>When the bubble pops up: a first puff of the theme's particles.</summary>
    private void Burst()
    {
        switch (Palette.Id)
        {
            case "ember": for (int i = 0; i < 16; i++) Ember(Rnd(0.1f, 0.9f), 1.6f); break;
            case "sakura": for (int i = 0; i < 9; i++) Petal(burst: true); break;
            case "toxic": for (int i = 0; i < 4; i++) Drip(); break;
            case "midnight": ShootingStar(); break;
        }
    }

    /// <summary>Move the particles, and make new ones for the theme: more while you or Dave talk.</summary>
    private void UpdateEffects(float voice, float t)
    {
        foreach (var s in sparks)
        {
            s.Life += 1 / 60f;
            s.Rot += s.Spin;
            switch (s.Kind)
            {
                case 'e': s.X += s.VX + MathF.Sin(s.Life * 9 + s.Size) * 0.35f; s.Y += s.VY; s.VY *= 0.985f; break; // embers drift up, wobbling
                case 'p': s.X += s.VX + MathF.Sin(s.Life * 2.4f + s.Size) * 0.5f; s.Y += s.VY; break;               // petals sway down
                case 'd': // drips: hang and grow, then fall
                    if (s.Life > 0.75f) { s.VY += 0.28f; s.Y += s.VY; }
                    break;
                default: s.X += s.VX; s.Y += s.VY; break;
            }
        }
        sparks.RemoveAll(s => s.Life >= s.Max || s.Y > size.Height / 2 + HaloMargin);
        if (Palette.Legacy || (contentAlpha < 0.3f && !IsCircle)) return;

        switch (Palette.Id)
        {
            case "ember": if (random.NextDouble() < 0.22 + voice * 1.1) Ember(Rnd(0.1f, 0.9f), 1f + voice); break;
            case "sakura": if (random.NextDouble() < 0.035 + voice * 0.12) Petal(burst: false); break;
            case "toxic": if (random.NextDouble() < 0.03 + voice * 0.09) Drip(); break;
            case "midnight": if (random.NextDouble() < 0.006 + voice * 0.02) ShootingStar(); break;
        }
    }

    private void Ember(float u, float force)
    {
        if (sparks.Count > 120) return;
        var edge = TopEdge(new RectangleF(-size.Width / 2, -size.Height / 2, size.Width, size.Height), Math.Min(size.Height / 2f, 34f), 6);
        var (p, n, _) = edge[(int)(u * (edge.Count - 1))];
        float speed = Rnd(0.7f, 1.6f) * force;
        sparks.Add(new Spark { Kind = 'e', X = p.X, Y = p.Y, VX = n.X * speed * 0.6f + Rnd(-0.25f, 0.25f), VY = Math.Min(n.Y, -0.4f) * speed, Max = Rnd(0.8f, 1.5f), Size = Rnd(1.1f, 2.3f) });
    }

    private void Petal(bool burst)
    {
        if (sparks.Count > 40) return;
        float halfW = size.Width / 2, halfH = size.Height / 2;
        sparks.Add(new Spark
        {
            Kind = 'p', X = Rnd(-halfW - 30, halfW), Y = burst ? Rnd(-halfH - 30, halfH) : -halfH - Rnd(20, 45),
            VX = Rnd(0.35f, 0.9f), VY = Rnd(0.3f, 0.75f), Max = Rnd(2.2f, 3.4f), Size = Rnd(3.5f, 5.5f), Rot = Rnd(0, 360), Spin = Rnd(-3f, 3f),
        });
    }

    private void Drip()
    {
        if (sparks.Count(s => s.Kind == 'd') > 8) return;
        var at = BottomPoint(Rnd(0.1f, 0.9f));
        sparks.Add(new Spark { Kind = 'd', X = at.X, Y = at.Y, Max = Rnd(1.6f, 2.2f), Size = Rnd(2.6f, 4f) });
    }

    private void ShootingStar()
    {
        float halfW = size.Width / 2 + 30;
        sparks.Add(new Spark { Kind = 's', X = Rnd(-halfW, halfW * 0.3f), Y = -size.Height / 2 - Rnd(10, 40), VX = Rnd(5.5f, 8.5f), VY = Rnd(1.2f, 2.4f), Max = 0.5f, Size = 1.6f });
    }

    private void DrawThemeFront(Graphics g, RectangleF shape, float t)
    {
        float cx = shape.X + shape.Width / 2, cy = shape.Y + shape.Height / 2;
        if (Palette.Id == "midnight") DrawTwinkles(g, shape, t);
        foreach (var s in sparks)
        {
            float x = cx + s.X, y = cy + s.Y, age = s.Life / s.Max, fade = Math.Clamp(1 - age, 0, 1);
            switch (s.Kind)
            {
                case 'e': // an ember: gold, turning orange and red as it cools, flickering, stretched along its flight
                {
                    var hot = Color.FromArgb(255, 236, 170);
                    var colour = age < 0.4f ? Mix(hot, Palette.Pink, age / 0.4f) : Mix(Palette.Pink, Palette.Purple, (age - 0.4f) / 0.6f);
                    int alpha = (int)(255 * fade * (0.65f + 0.35f * MathF.Sin(s.Life * 38 + s.Size * 5)));
                    using (var glowBrush = new SolidBrush(Palette.WithAlpha(colour, alpha / 3))) g.FillEllipse(glowBrush, x - s.Size * 3, y - s.Size * 3, s.Size * 6, s.Size * 6);
                    using (var core = new SolidBrush(Palette.WithAlpha(colour, alpha))) g.FillEllipse(core, x - s.Size * 0.6f, y - s.Size * 1.5f, s.Size * 1.2f, s.Size * 3f);
                    break;
                }
                case 'p': // a cherry blossom petal, turning as it falls
                {
                    int alpha = (int)(230 * Math.Min(1, s.Life * 3) * fade);
                    var state = g.Save();
                    g.TranslateTransform(x, y);
                    g.RotateTransform(s.Rot);
                    using var petal = new GraphicsPath();
                    float l = s.Size * 1.6f, w = s.Size;
                    petal.AddBezier(0, -l, w, -l * 0.4f, w, l * 0.5f, 0, l);
                    petal.AddBezier(0, l, -w, l * 0.5f, -w, -l * 0.4f, 0, -l);
                    using var fill = new LinearGradientBrush(new RectangleF(-w, -l, w * 2, l * 2), Palette.WithAlpha(Palette.Cyan, alpha), Palette.WithAlpha(Palette.Purple, alpha), 90f);
                    g.FillPath(fill, petal);
                    g.Restore(state);
                    break;
                }
                case 'd': // a drop of glowing slime: hangs and swells, then falls, stretching
                {
                    float grow = Math.Min(1, s.Life / 0.75f), r = s.Size * (0.4f + 0.6f * grow);
                    float stretch = s.VY > 0 ? Math.Min(1.8f, 1 + s.VY * 0.08f) : 1 + 0.25f * grow;
                    int alpha = (int)(240 * Math.Min(1, fade * 2));
                    if (s.Life <= 0.75f || s.VY < 3) // the thread of slime back to the edge
                    {
                        using var neck = new Pen(Palette.WithAlpha(Palette.Purple, alpha / 2), Math.Max(1, r * 0.6f));
                        g.DrawLine(neck, cx + s.X, cy + s.Y - r * 2, x, y);
                    }
                    using (var glowBrush = new SolidBrush(Palette.WithAlpha(Palette.Pink, alpha / 4))) g.FillEllipse(glowBrush, x - r * 2.4f, y - r * 2.4f * stretch, r * 4.8f, r * 4.8f * stretch);
                    using (var drop = new SolidBrush(Palette.WithAlpha(Palette.Pink, alpha))) g.FillEllipse(drop, x - r, y - r * stretch, r * 2, r * 2 * stretch);
                    using (var shine = new SolidBrush(Color.FromArgb(alpha * 2 / 3, 240, 255, 230))) g.FillEllipse(shine, x - r * 0.45f, y - r * stretch * 0.55f, r * 0.55f, r * 0.55f);
                    break;
                }
                case 's': // a shooting star: a bright head with a long fading tail
                {
                    int alpha = (int)(255 * Math.Sin(Math.PI * Math.Clamp(age, 0, 1)));
                    var tail = new PointF(x - s.VX * 9, y - s.VY * 9);
                    using (var brush = new LinearGradientBrush(new PointF(x, y), tail, Color.FromArgb(alpha, 255, 255, 255), Color.Transparent))
                    using (var pen = new Pen(brush, 1.8f)) g.DrawLine(pen, x, y, tail.X, tail.Y);
                    using (var head = new SolidBrush(Palette.WithAlpha(Palette.Cyan, alpha / 2))) g.FillEllipse(head, x - 4, y - 4, 8, 8);
                    break;
                }
            }
        }
    }

    /// <summary>Midnight: a few stars around the bubble, twinkling at their own pace.</summary>
    private void DrawTwinkles(Graphics g, RectangleF shape, float t)
    {
        for (int i = 0; i < 9; i++)
        {
            float u = (i * 0.618f) % 1, x = shape.X - 30 + u * (shape.Width + 60), y = shape.Y - 18 - (i * 37 % 26);
            float twinkle = Smooth(t * (1.3f + i * 0.21f) + i * 2), s = 0.6f + 1.4f * twinkle;
            int alpha = (int)(220 * twinkle * contentAlphaOrOne());
            using var star = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
            g.FillEllipse(star, x - s, y - s, s * 2, s * 2);
            using var glint = new Pen(Color.FromArgb(alpha / 3, 255, 255, 255), 0.8f);
            g.DrawLine(glint, x - s * 3, y, x + s * 3, y);
            g.DrawLine(glint, x, y - s * 3, x, y + s * 3);
        }
    }

    private static Color Mix(Color a, Color b, float f)
    {
        f = Math.Clamp(f, 0, 1);
        return Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
    }
}
