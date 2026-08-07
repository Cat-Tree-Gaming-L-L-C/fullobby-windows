// Fullobby brand-art generator.
// Renders the app icon glyph (ascending silver→blue lobby bars over a full
// capacity meter, dark tile) and the text logo, using the repo's bundled
// Bebas Neue and brand palette.
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
var blue      = Color.FromArgb(0x4A, 0x90, 0xD9);
var blueHi    = Color.FromArgb(0x62, 0xA5, 0xE8);
var blueDeep  = Color.FromArgb(0x3B, 0x79, 0xB8);
var silver    = Color.FromArgb(0xC9, 0xCE, 0xD6);
var silverHi  = Color.FromArgb(0xE2, 0xE6, 0xEC);
var silverDeep= Color.FromArgb(0x9A, 0xA3, 0xB0);
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

// ── Icon glyph: dark rounded tile, ascending lobby bars, full capacity meter ────
// The mark: a lobby filling with players — three bars stepping up (silver → blue),
// over the full capacity meter. Game-neutral by design (docs/MULTI-GAME.md).
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

    // Ascending bars: baseline at 0.60, heights step up left→right, colors run
    // silver → light blue → blue so the fill "arrives" as the lobby fills.
    float baseY = S * 0.60f;
    float barW = S * 0.16f, gap = S * 0.06f, x0 = S * 0.20f;
    (float h, Color top, Color bottom)[] bars =
    [
        (S * 0.16f, silverHi, silver),
        (S * 0.25f, blueHi, blue),
        (S * 0.34f, blue, blueDeep),
    ];
    for (int i = 0; i < bars.Length; i++)
    {
        var (h, top, bottom) = bars[i];
        var r = new RectangleF(x0 + i * (barW + gap), baseY - h, barW, h);
        // Gradient rect padded a hair — LinearGradientBrush edge artifacts show at small sizes.
        using var fill = new LinearGradientBrush(
            new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), top, bottom, 90f);
        using var path = RoundedRect(r, barW * 0.32f);
        g.FillPath(fill, path);
    }

    // Capacity bar, 100% full, with tick marks so it reads as a meter (not a stripe).
    var barRect = new RectangleF(S * 0.20f, S * 0.70f, S * 0.60f, S * 0.115f);
    using var barFill = new LinearGradientBrush(barRect, blueHi, blueDeep, 90f);
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

// ── Wordmark: FULLOBBY + full pip meter on a dark card ──────────────────────────
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

    // "FULLOBBY" in Bebas Neue with light tracking; FULL blue, OBBY silver-white.
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

    using (var blueText = new LinearGradientBrush(
        new RectangleF(0, ty, W, em), blueHi, blue, 90f))
    {
        DrawRun(full, blueText);
    }
    using (var white = new SolidBrush(textMain))
        DrawRun(obby, white);

    // Full pip meter under the text — ten blue player pips in a dark track (a full
    // lobby, 100/100), with a "100/100" tally at the right.
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

    // Rounded pips sized off the per-slot pitch so they never overlap. The first
    // pips are silver, the rest blue — the same silver→blue fill-up as the glyph.
    int n = 10;
    float inset = trackH * 0.55f;
    float pitch = (trackW - 2 * inset) / n;
    float pw = pitch * 0.40f;
    float ph = trackH * 0.58f;
    using (var silverPip = new SolidBrush(silverDeep))
    using (var bluePip = new SolidBrush(blue))
    {
        for (int i = 0; i < n; i++)
        {
            float px = trackX + inset + pitch * i + (pitch - pw) / 2;
            float py = trackY + (trackH - ph) / 2;
            using var pp = RoundedRect(new RectangleF(px, py, pw, ph), pw / 2);
            g.FillPath(i < 3 ? silverPip : bluePip, pp);
        }
    }

    using (var blueBrush = new SolidBrush(blue))
    {
        using var p = new GraphicsPath();
        p.AddString(tally, bebas, (int)FontStyle.Regular, tallyFont.Size,
            new PointF(trackX + trackW + gap, trackY + (trackH - tallySz.Height) / 2), sf);
        g.FillPath(blueBrush, p);
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
