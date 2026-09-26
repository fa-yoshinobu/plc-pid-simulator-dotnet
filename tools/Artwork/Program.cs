using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// PID Process Simulator のアートワーク
//   src/PidSimulator.App/Assets/AppIcon.ico / AppIcon.png : アプリアイコン
//   docs/images/social-preview.png : GitHub のソーシャルプレビュー・README 見出し（1280×640）
//
// アプリアイコン
// 実行: dotnet run --project tools/Artwork
// 青い角丸の地に、SP（破線）へ向かって行き過ぎてから落ち着く PV のステップ応答（白）と、MV 色（橙）の点。
// 大きいサイズは 256 基準の座標をそのまま縮小し、16/24 px は線を太くして破線をやめた簡略版にする。

string root = FindRepoRoot();
string assets = Path.Combine(root, "src", "PidSimulator.App", "Assets");
Directory.CreateDirectory(assets);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = sizes.Select(s => (Size: s, Png: Render(s))).ToList();

File.WriteAllBytes(Path.Combine(assets, "AppIcon.ico"), BuildIco(pngs));
File.WriteAllBytes(Path.Combine(assets, "AppIcon.png"), pngs.First(p => p.Size == 256).Png);
string docs = Path.Combine(root, "docs", "images");
Directory.CreateDirectory(docs);
File.WriteAllBytes(Path.Combine(docs, "social-preview.png"), RenderSocialPreview());
Console.WriteLine($"social-preview.png (1280x640) -> {docs}");
string preview = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "pidsim-icon-preview.png");
File.WriteAllBytes(preview, RenderPreview(sizes));
Console.WriteLine($"AppIcon.ico ({string.Join(", ", sizes)} px) -> {assets}");
Console.WriteLine($"preview -> {preview}");

static byte[] Render(int size) => EncodeSquare(Draw(size), size);

static DrawingVisual Draw(int size)
{
    var v = new DrawingVisual();
    using var dc = v.RenderOpen();
    double k = size / 256.0;
    bool small = size <= 24;

    dc.PushTransform(new ScaleTransform(k, k));

    // 地：角丸の青（上がやや明るいグラデーション）
    var bg = new LinearGradientBrush(Color.FromRgb(0x2A, 0x6F, 0xB8), Color.FromRgb(0x15, 0x47, 0x7C), 90);
    double inset = small ? 4 : 8, radius = small ? 44 : 52;
    dc.DrawRoundedRectangle(bg, null, new Rect(inset, inset, 256 - 2 * inset, 256 - 2 * inset), radius, radius);

    // SP：目標値の水平線
    double spY = small ? 104 : 110;
    var spPen = new Pen(new SolidColorBrush(Color.FromArgb(small ? (byte)0x90 : (byte)0xB0, 0xCF, 0xE2, 0xF6)), small ? 14 : 9)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    };
    if (!small) spPen.DashStyle = new DashStyle([2.2, 2.0], 0);
    dc.DrawLine(spPen, new Point(small ? 44 : 44, spY), new Point(212, spY));

    // PV：ステップ応答（立ち上がり → 行き過ぎ → 整定）
    var pv = new StreamGeometry();
    using (var g = pv.Open())
    {
        if (small)
        {
            g.BeginFigure(new Point(44, 210), false, false);
            g.BezierTo(new Point(92, 210), new Point(92, 76), new Point(136, 82), true, true);
            g.BezierTo(new Point(170, 88), new Point(170, spY), new Point(210, spY), true, true);
        }
        else
        {
            g.BeginFigure(new Point(44, 206), false, false);
            g.BezierTo(new Point(88, 206), new Point(92, 72), new Point(128, 76), true, true);
            g.BezierTo(new Point(156, 80), new Point(158, 126), new Point(182, 118), true, true);
            g.BezierTo(new Point(196, 113), new Point(202, spY), new Point(212, spY), true, true);
        }
    }
    var pvPen = new Pen(Brushes.White, small ? 26 : 17) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    dc.DrawGeometry(null, pvPen, pv);

    // MV 色の点：応答が目標に落ち着いた位置
    if (!small)
    {
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x28)), new Pen(Brushes.White, 7), new Point(212, spY), 19, 19);
    }

    dc.Pop();
    return v;
}

static byte[] EncodeSquare(Visual visual, int size) => EncodeImage(visual, size, size);

static byte[] EncodeImage(Visual visual, int width, int height)
{
    var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

// PNG 圧縮エントリの ICO（Windows Vista 以降で有効）
static byte[] BuildIco(List<(int Size, byte[] Png)> images)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
    return ms.ToArray();
}

// 確認用：明るい背景と暗い背景に各サイズを実寸で並べ、右端に 16/32 px を 8 倍に拡大して表示
static byte[] RenderPreview(int[] sizes)
{
    const int W = 900, H = 420;
    var v = new DrawingVisual();
    using (var dc = v.RenderOpen())
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8)), null, new Rect(0, 0, W, H / 2));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2A)), null, new Rect(0, H / 2, W, H / 2));
        foreach (int band in new[] { 0, H / 2 })
        {
            double x = 20;
            foreach (int s in sizes.Where(s => s <= 128))
            {
                var img = Decode(Render(s));
                dc.DrawImage(img, new Rect(x, band + (H / 2 - s) / 2.0, s, s));
                x += s + 24;
            }
        }
        var big16 = Decode(Render(16));
        var big32 = Decode(Render(32));
        RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.NearestNeighbor);
        dc.DrawImage(big16, new Rect(W - 290, 22, 128, 128));
        dc.DrawImage(big32, new Rect(W - 150, 22, 128, 128));
        dc.DrawImage(Decode(Render(256)), new Rect(W - 290, H / 2 + 20, 170, 170));
    }
    return EncodeImage(v, W, H);
}

static BitmapSource Decode(byte[] png)
{
    var d = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    return d.Frames[0];
}

// ---- GitHub ソーシャルプレビュー（1280×640） ----

static byte[] RenderSocialPreview()
{
    const int W = 1280, H = 640;
    var white = Colors.White;
    var soft = Color.FromRgb(0xD5, 0xE6, 0xF7);
    var muted = Color.FromRgb(0x93, 0xB5, 0xD8);
    var orange = Color.FromRgb(0xF2, 0x8C, 0x28);

    var v = new DrawingVisual();
    using (var dc = v.RenderOpen())
    {
        // 地：アイコンと同じ青を暗くしたグラデーション＋トレンド画面の方眼
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1A, 0x4B, 0x80), Color.FromRgb(0x0C, 0x24, 0x40), new Point(0, 0), new Point(1, 1)), null, new Rect(0, 0, W, H));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), 1);
        for (int x = 0; x <= W; x += 40) dc.DrawLine(grid, new Point(x + 0.5, 0), new Point(x + 0.5, H));
        for (int y = 0; y <= H; y += 40) dc.DrawLine(grid, new Point(0, y + 0.5), new Point(W, y + 0.5));

        // 左：アイコン・名前・説明
        dc.DrawImage(Decode(Render(256)), new Rect(80, 92, 112, 112));
        Text(dc, "PID Process Simulator", 78, 226, 62, FontWeights.SemiBold, white, "Segoe UI");
        Text(dc, "PLC の PID を、実設備なしで確かめる。", 82, 314, 30, FontWeights.Normal, soft);
        double row1 = Chip(dc, "MELSEC SLMP 3E/4E", 82, 382);
        Chip(dc, "GX Simulator 3", row1 + 12, 382);
        Chip(dc, "FORCE・外乱・トレンド", 82, 432);
        Text(dc, "Windows  ·  .NET 9 / WPF  ·  MIT License", 82, 540, 20, FontWeights.Normal, muted, "Segoe UI");

        // 右：制御対象の詳細画面を簡略化したパネル
        var panel = new Rect(728, 92, 472, 456);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x10, 0x25)), new Pen(new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), 1.5), panel, 18, 18);
        Bubble(dc, new Point(776, 138), "LIC", "101");
        Text(dc, "LIC-101 原水槽 液面", 810, 122, 24, FontWeights.SemiBold, white);
        StatusPill(dc, "制御中", 1100, 124);

        var (sp, pv, mv) = SimulatePiStep();
        int last = pv.Length - 1;
        double vx = 764;
        foreach (var (label, value, unit, color) in new[]
        {
            ("SP", sp[last] * 100, "%", soft), ("PV", pv[last] * 100, "%", white), ("MV", mv[last] * 100, "%", orange),
        })
        {
            Text(dc, label, vx, 180, 15, FontWeights.SemiBold, color, "Segoe UI");
            Text(dc, value.ToString("0.0"), vx, 198, 34, FontWeights.Normal, white, "Consolas");
            Text(dc, unit, vx + 88, 214, 16, FontWeights.Normal, muted, "Segoe UI");
            vx += 140;
        }

        var ca = new Rect(764, 268, 400, 232);
        var axis = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(ca.Top + ca.Height * i / 4) + 0.5;
            dc.DrawLine(axis, new Point(ca.Left, y), new Point(ca.Right, y));
        }
        Point P(int i, double value) => new(ca.Left + ca.Width * i / last, ca.Bottom - value * ca.Height);

        var spPen = new Pen(new SolidColorBrush(soft), 3) { DashStyle = new DashStyle([3, 2.2], 0) };
        dc.DrawGeometry(null, spPen, Poly(sp.Length, i => P(i, sp[i]), step: true));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xE6, orange.R, orange.G, orange.B)), 3) { LineJoin = PenLineJoin.Round }, Poly(mv.Length, i => P(i, mv[i])));
        dc.DrawGeometry(null, new Pen(Brushes.White, 5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Poly(pv.Length, i => P(i, pv[i])));
        dc.DrawEllipse(Brushes.White, null, P(last, pv[last]), 7, 7);

        double lx = 764;
        foreach (var (label, color, dashed) in new[] { ("SP", soft, true), ("PV", white, false), ("MV", orange, false) })
        {
            var pen = new Pen(new SolidColorBrush(color), 3);
            if (dashed) pen.DashStyle = new DashStyle([2, 1.5], 0);
            dc.DrawLine(pen, new Point(lx, 523), new Point(lx + 22, 523));
            Text(dc, label, lx + 30, 512, 16, FontWeights.SemiBold, color, "Segoe UI");
            lx += 90;
        }
    }
    return EncodeImage(v, W, H);
}

/// <summary>一次遅れ＋むだ時間のプロセスを PI 制御し、SP をステップ変化させたときの SP・PV・MV（0〜1）</summary>
static (double[] Sp, double[] Pv, double[] Mv) SimulatePiStep()
{
    // プロセス：x' = (K·u + bias − x) / τ、むだ時間あり。PI はやや強めにして1回行き過ぎる応答にする
    const double dt = 0.05, tau = 5, dead = 1.4, gain = 1.4, bias = -0.1, kp = 2.6, ti = 3.6;
    const int n = 720;
    var sp = new double[n];
    var pv = new double[n];
    var mv = new double[n];
    double x = 0.35, u0 = (x - bias) / gain, integ = u0 / kp;
    var delay = new Queue<double>(Enumerable.Repeat(u0, (int)(dead / dt)));
    for (int i = 0; i < n; i++)
    {
        double s = i * dt < 5 ? 0.35 : 0.65;
        double e = s - x;
        double newInteg = integ + e * dt / ti;
        double u = kp * (e + newInteg);
        if (u is >= 0 and <= 1) integ = newInteg;
        u = Math.Clamp(u, 0, 1);
        delay.Enqueue(u);
        x += (gain * delay.Dequeue() + bias - x) * dt / tau;
        sp[i] = s;
        pv[i] = x;
        mv[i] = u;
    }    return (sp, pv, mv);
}

static StreamGeometry Poly(int count, Func<int, Point> point, bool step = false)
{
    var g = new StreamGeometry();
    using (var ctx = g.Open())
    {
        ctx.BeginFigure(point(0), false, false);
        for (int i = 1; i < count; i++)
        {
            var p = point(i);
            if (step) ctx.LineTo(new Point(p.X, point(i - 1).Y), true, false);
            ctx.LineTo(p, true, true);
        }
    }
    g.Freeze();
    return g;
}

static void Text(DrawingContext dc, string text, double x, double y, double size, FontWeight weight, Color color, string family = "Yu Gothic UI")
{
    var ft = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
        new Typeface(new FontFamily($"{family}, Yu Gothic UI, Meiryo UI"), FontStyles.Normal, weight, FontStretches.Normal),
        size, new SolidColorBrush(color), 1.0);
    dc.DrawText(ft, new Point(x, y));
}

static double MeasureWidth(string text, double size, FontWeight weight)
{
    var ft = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Yu Gothic UI, Meiryo UI"), FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, 1.0);
    return ft.WidthIncludingTrailingWhitespace;
}

/// <summary>枠付きの小さなラベル。右端のX座標を返す。</summary>
static double Chip(DrawingContext dc, string text, double x, double y)
{
    double w = MeasureWidth(text, 19, FontWeights.SemiBold) + 32;
    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), new Pen(new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF)), 1.5),
        new Rect(x, y, w, 38), 19, 19);
    Text(dc, text, x + 16, y + 6, 19, FontWeights.SemiBold, Colors.White);
    return x + w;
}

static void StatusPill(DrawingContext dc, string text, double x, double y)
{
    double w = MeasureWidth(text, 15, FontWeights.SemiBold) + 38;
    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xDF, 0xF1, 0xE5)), null, new Rect(x - w + 64, y, w, 28), 14, 14);
    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x16, 0x73, 0x3C)), null, new Point(x - w + 64 + 15, y + 14), 4.5, 4.5);
    Text(dc, text, x - w + 64 + 26, y + 4, 15, FontWeights.SemiBold, Color.FromRgb(0x16, 0x73, 0x3C));
}

/// <summary>計装タグの丸（盤面計器：円＋水平線）</summary>
static void Bubble(DrawingContext dc, Point c, string letters, string number)
{
    var pen = new Pen(Brushes.White, 2);
    dc.DrawEllipse(null, pen, c, 24, 24);
    dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(c.X - 24, c.Y), new Point(c.X + 24, c.Y));
    double lw = MeasureWidth(letters, 13, FontWeights.SemiBold), nw = MeasureWidth(number, 13, FontWeights.Normal);
    Text(dc, letters, c.X - lw / 2, c.Y - 19, 13, FontWeights.SemiBold, Colors.White, "Consolas");
    Text(dc, number, c.X - nw / 2, c.Y + 2, 13, FontWeights.Normal, Color.FromRgb(0xD5, 0xE6, 0xF7), "Consolas");
}
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PidSimulator.sln"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("PidSimulator.sln が見つかりません");
}
