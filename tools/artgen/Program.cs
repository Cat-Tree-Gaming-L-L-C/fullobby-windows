// Fullobby brand-art generator.
// Renders the app icon glyph (gold helmet over a full capacity meter, dark tile)
// and the text logo, using the repo's bundled Bebas Neue and brand palette.
// Overwrites icons/* and assets/logo_*.png in place:  dotnet run --project tools/artgen
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

// Repo root: first argument if given, else the nearest ancestor holding src/Fullobby.sln.
string Repo = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepoRoot();
static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "src", "Fullobby.sln")))
            return d.FullName;
    throw new InvalidOperationException("Repo root not found (looked for src/Fullobby.sln above the tool binary); pass it as an argument.");
}

// Brand palette (src/Fullobby.App/Themes/Brand.xaml)
var gold      = Color.FromArgb(0xC9, 0xA2, 0x27);
var goldHi    = Color.FromArgb(0xDB, 0xB4, 0x2F);
var goldDeep  = Color.FromArgb(0xB0, 0x8E, 0x22);
var bgDark    = Color.FromArgb(0x0A, 0x0C, 0x0F);
var bgCard    = Color.FromArgb(0x14, 0x17, 0x1D);
var bgTrack   = Color.FromArgb(0x1A, 0x1E, 0x25);
var border    = Color.FromArgb(0x2A, 0x2F, 0x38);
var textMain  = Color.FromArgb(0xE8, 0xE9, 0xEB);

var fonts = new PrivateFontCollection();
fonts.AddFontFile(Path.Combine(Repo, @"assets\fonts\BebasNeue-Regular.ttf"));
var bebas = fonts.Families[0];

static GraphicsPath RoundedRect(RectangleF r, float rad)
{
    var p = new GraphicsPath();
    float d = rad * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static GraphicsPath Star(float cx, float cy, float rOuter, float rInner)
{
    var pts = new PointF[10];
    for (int i = 0; i < 10; i++)
    {
        double a = -Math.PI / 2 + i * Math.PI / 5;
        float r = i % 2 == 0 ? rOuter : rInner;
        pts[i] = new PointF(cx + (float)(r * Math.Cos(a)), cy + (float)(r * Math.Sin(a)));
    }
    var p = new GraphicsPath();
    p.AddPolygon(pts);
    return p;
}

// Helmet (dome + brim), drawn into rect (x..x+w horizontally), dome top at y, brim bottom at y+h.
static void DrawHelmet(Graphics g, float x, float y, float w, float h, Brush dome, Brush brim, Color? starColor, float starScale = 0.13f)
{
    float brimH = h * 0.22f;
    float domeW = w * 0.84f;
    float domeX = x + (w - domeW) / 2;
    float domeH = (h - brimH * 0.55f) * 2;               // full-ellipse height; we use the top half
    using (var dp = new GraphicsPath())
    {
        dp.AddArc(domeX, y, domeW, domeH, 180, 180);      // top half of ellipse
        dp.CloseFigure();
        g.FillPath(dome, dp);
    }
    float brimY = y + h - brimH;
    using (var bp = RoundedRect(new RectangleF(x, brimY, w, brimH), brimH / 2))
        g.FillPath(brim, bp);
    if (starColor is Color sc)
    {
        float r = w * starScale;
        using var sp = Star(x + w / 2, y + h * 0.48f, r, r * 0.42f);
        using var sb = new SolidBrush(sc);
        g.FillPath(sb, sp);
    }
}

// ── Icon glyph: dark rounded tile, gold helmet, full capacity bar ────────────────
Bitmap RenderGlyph(int size, bool tile = true)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    float S = size;

    if (tile)
    {
        var tileRect = new RectangleF(0, 0, S, S);
        using var tp = RoundedRect(tileRect, S * 0.225f);
        using var tileFill = new LinearGradientBrush(tileRect, bgCard, bgDark, 90f);
        g.FillPath(tileFill, tp);
        if (S >= 24)
        {
            using var pen = new Pen(border, Math.Max(1f, S * 0.016f));
            pen.Alignment = PenAlignment.Inset;
            g.DrawPath(pen, tp);
        }
    }

    // Helmet: wide, flat M1-style dome + brim in the upper-middle of the tile.
    var helmRect = new RectangleF(S * 0.17f, S * 0.26f, S * 0.66f, S * 0.32f);
    using var domeBrush = new LinearGradientBrush(
        new RectangleF(0, helmRect.Y, S, helmRect.Height), goldHi, gold, 90f);
    using var brimBrush = new SolidBrush(goldDeep);
    DrawHelmet(g, helmRect.X, helmRect.Y, helmRect.Width, helmRect.Height,
        domeBrush, brimBrush, S >= 30 ? bgDark : null);

    // Capacity bar, 100% full, with tick marks so it reads as a meter (not a stripe).
    var barRect = new RectangleF(S * 0.20f, S * 0.70f, S * 0.60f, S * 0.115f);
    using var barFill = new LinearGradientBrush(barRect, goldHi, goldDeep, 90f);
    using var barPath = RoundedRect(barRect, barRect.Height / 2);
    g.FillPath(barFill, barPath);
    if (S >= 30)
    {
        using var tick = new Pen(Color.FromArgb(170, bgDark), Math.Max(1f, S * 0.018f));
        for (int i = 1; i < 4; i++)
        {
            float txx = barRect.X + barRect.Width * i / 4f;
            g.DrawLine(tick, txx, barRect.Y + barRect.Height * 0.15f,
                             txx, barRect.Bottom - barRect.Height * 0.15f);
        }
    }

    return bmp;
}

// ── Wordmark: FULLOBBY + full helmet meter on a dark card ───────────────────────
Bitmap RenderWordmark(int wpx, int hpx)
{
    var bmp = new Bitmap(wpx, hpx, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.TextRenderingHint = TextRenderingHint.AntiAlias;

    float W = wpx, H = hpx, m = H * 0.05f;
    var card = new RectangleF(m, m, W - 2 * m, H - 2 * m);
    using (var cp = RoundedRect(card, H * 0.10f))
    {
        using var cardFill = new LinearGradientBrush(card, bgCard, bgDark, 90f);
        g.FillPath(cardFill, cp);
        using var pen = new Pen(border, H * 0.010f) { Alignment = PenAlignment.Inset };
        g.DrawPath(pen, cp);
    }

    // "FULLOBBY" in Bebas Neue with light tracking; FULL gold, OBBY off-white.
    float em = H * 0.475f;
    using var font = new Font(bebas, em, FontStyle.Regular, GraphicsUnit.Pixel);
    var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
    float tracking = em * 0.055f;

    float MeasureRun(string s)
    {
        float w = 0;
        foreach (char c in s)
            w += g.MeasureString(c.ToString(), font, PointF.Empty, sf).Width + tracking;
        return w - tracking;
    }

    string full = "FULL", obby = "OBBY";
    float totalW = MeasureRun(full + obby);
    float tx = (W - totalW) / 2;
    float ty = H * 0.14f;

    void DrawRun(string s, Brush b)
    {
        foreach (char c in s)
        {
            using var p = new GraphicsPath();
            p.AddString(c.ToString(), bebas, (int)FontStyle.Regular, em, new PointF(tx, ty), sf);
            g.FillPath(b, p);
            tx += g.MeasureString(c.ToString(), font, PointF.Empty, sf).Width + tracking;
        }
    }

    using (var goldText = new LinearGradientBrush(
        new RectangleF(0, ty, W, em), goldHi, gold, 90f))
    {
        DrawRun(full, goldText);
    }
    using (var white = new SolidBrush(textMain))
        DrawRun(obby, white);

    // Full helmet meter under the text — ten gold helmets in a dark track (the old
    // badge's meter, finally at 100/100), with a "100/100" tally at the right.
    using var tallyFont = new Font(bebas, H * 0.145f, FontStyle.Regular, GraphicsUnit.Pixel);
    string tally = "100/100";
    var tallySz = g.MeasureString(tally, tallyFont, PointF.Empty, sf);

    float gap = W * 0.015f;
    float trackW = totalW - tallySz.Width - gap;
    float trackH = H * 0.155f;
    float trackX = (W - totalW) / 2;
    float trackY = H * 0.70f;
    var track = new RectangleF(trackX, trackY, trackW, trackH);
    using (var trp = RoundedRect(track, trackH / 2))
    {
        using var tb = new SolidBrush(bgTrack);
        g.FillPath(tb, trp);
        using var pen = new Pen(border, H * 0.008f) { Alignment = PenAlignment.Inset };
        g.DrawPath(pen, trp);
    }

    // Size the mini helmets off the per-slot pitch so they never overlap.
    int n = 10;
    float inset = trackH * 0.55f;
    float pitch = (trackW - 2 * inset) / n;
    float hw = pitch * 0.68f;
    float hh = Math.Min(hw / 1.15f, trackH * 0.62f);
    using (var hb = new SolidBrush(gold))
    using (var hbrim = new SolidBrush(goldDeep))
    {
        for (int i = 0; i < n; i++)
        {
            float hx = trackX + inset + pitch * i + (pitch - hw) / 2;
            float hy = trackY + (trackH - hh) / 2;
            DrawHelmet(g, hx, hy, hw, hh, hb, hbrim, null);
        }
    }

    using (var goldBrush = new SolidBrush(gold))
    {
        using var p = new GraphicsPath();
        p.AddString(tally, bebas, (int)FontStyle.Regular, tallyFont.Size,
            new PointF(trackX + trackW + gap, trackY + (trackH - tallySz.Height) / 2), sf);
        g.FillPath(goldBrush, p);
    }

    return bmp;
}

// ── ICO container (PNG-compressed entries) ──────────────────────────────────────
static void WriteIco(string path, IReadOnlyList<(int size, byte[] png)> images)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
}

static byte[] PngBytes(Bitmap b)
{
    using var ms = new MemoryStream();
    b.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

void SaveGlyph(int size, string relPath)
{
    using var b = RenderGlyph(size);
    b.Save(Path.Combine(Repo, relPath), ImageFormat.Png);
    Console.WriteLine($"{relPath}  {size}x{size}");
}

// Icon PNG set
SaveGlyph(512, @"icons\icon.png");
SaveGlyph(32,  @"icons\32x32.png");
SaveGlyph(128, @"icons\128x128.png");
SaveGlyph(256, @"icons\128x128@2x.png");
SaveGlyph(50,  @"icons\StoreLogo.png");
foreach (int s in new[] { 30, 44, 71, 89, 107, 142, 150, 284, 310 })
    SaveGlyph(s, $@"icons\Square{s}x{s}Logo.png");

// icon.ico
var icoSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var entries = new List<(int, byte[])>();
foreach (int s in icoSizes)
{
    using var b = RenderGlyph(s);
    entries.Add((s, PngBytes(b)));
}
WriteIco(Path.Combine(Repo, @"icons\icon.ico"), entries);
Console.WriteLine(@"icons\icon.ico  " + string.Join(",", icoSizes));

// In-app glyph (titlebar/About/onboarding)
using (var b = RenderGlyph(1024))
    b.Save(Path.Combine(Repo, @"assets\logo_no_text.png"), ImageFormat.Png);
Console.WriteLine(@"assets\logo_no_text.png  1024x1024");

// Text logo
using (var b = RenderWordmark(1800, 620))
    b.Save(Path.Combine(Repo, @"assets\logo_text.png"), ImageFormat.Png);
Console.WriteLine(@"assets\logo_text.png  1800x620");

Console.WriteLine("done");
