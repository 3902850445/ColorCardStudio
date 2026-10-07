using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;

namespace ColorCardStudio.Core;

public sealed record ExtractedColor(
    string Hex,
    int R, int G, int B,
    double C, double M, double Y, double K,
    double Hue, double Saturation, double Lightness,
    double LabL, double LabA, double LabB,
    int PixelCount,
    double Coverage);

public sealed record ExtractionResult(
    IReadOnlyList<ExtractedColor> Colors,
    string SourceColorSpace,
    bool IsCmykSource,
    string? IccProfileName,
    int ImageWidth,
    int ImageHeight,
    int TotalPixelsAnalyzed,
    double TotalInkCoverage,
    string StandardId,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 图片取色引擎。
/// 印刷稿（CMYK JPEG）在解码时会被 ImageSharp 转换为 RGB，因此这里额外做源色彩空间探测：
/// 读取 JPEG 的 Adobe APP14 标记与 ICC 描述文件签名，区分「CMYK 原稿」与「RGB 屏幕稿」，
/// 并在结果中如实标注，避免把屏幕观感色误当作印刷墨值。
/// </summary>
public sealed class ColorExtractor
{
    public ExtractionResult Extract(string path, int maxColors = 16, string standardId = "pso-coated-v3")
    {
        var std = PrintStandards.ById(standardId);
        using var fs = File.OpenRead(path);
        using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(fs);

        int w = img.Width, h = img.Height;
        string space = DetectColorSpace(path, img.Metadata, out bool isCmyk, out string? iccName);

        var warnings = new List<string>();
        long totalPixels = (long)w * h;
        int step = totalPixels > 400_000 ? (int)Math.Ceiling(Math.Sqrt(totalPixels / 400_000.0)) : 1;

        if (step > 1)
            warnings.Add($"图片较大（{w}×{h}），已按 1:{step} 抽样以保证响应速度");

        var pixels = new List<RgbSample>((int)Math.Min(totalPixels, 400_000L) + 16);

        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y += step)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x += step)
                {
                    var p = row[x];
                    if (p.A < 128) continue; // 跳过透明像素，避免 PNG 透明底产生脏色
                    pixels.Add(new RgbSample(p.R, p.G, p.B));
                }
            }
        });

        if (pixels.Count == 0)
            return new ExtractionResult(Array.Empty<ExtractedColor>(), space, isCmyk, iccName,
                w, h, 0, 0, standardId, new[] { "图片为全透明或无有效像素" });

        var clusters = Quantize(pixels, maxColors);
        int analyzed = pixels.Count;
        double inkSum = 0;
        var results = new List<ExtractedColor>(clusters.Count);

        foreach (var cl in clusters)
        {
            var rgb = new ColorMath.Rgb(cl.R, cl.G, cl.B);
            var cmyk = std.ToCmyk(rgb);
            var lab = ColorMath.RgbToLab(rgb);
            var hsl = ColorMath.RgbToHsl(rgb);
            inkSum += PrintStandard.TotalInk(cmyk) * cl.Count / analyzed;
            results.Add(new ExtractedColor(
                ColorMath.ToHex(rgb), cl.R, cl.G, cl.B,
                cmyk.C, cmyk.M, cmyk.Y, cmyk.K,
                hsl.H, hsl.S, hsl.L,
                lab.L, lab.A, lab.B,
                cl.Count, 100.0 * cl.Count / analyzed));
        }

        results = results.OrderByDescending(c => c.Coverage).ToList();

        if (isCmyk)
            warnings.Add("检测到 CMYK 原稿：色值为按 Adobe 逆变换还原的印刷墨，非屏幕观感色");
        if (!string.IsNullOrEmpty(iccName))
            warnings.Add($"内嵌 ICC 色彩空间签名：{iccName}");
        else
            warnings.Add($"未内嵌 ICC 描述文件，CMYK 为按「{std.Name}」特性的模型换算值");

        return new ExtractionResult(results, space, isCmyk, iccName, w, h, analyzed,
            inkSum, standardId, warnings);
    }

    readonly record struct RgbSample(byte R, byte G, byte B);

    /// <summary>
    /// 探测色彩空间。CMYK JPEG 的判据（按可靠度排序）：
    /// 1) 内嵌 ICC 的 DataColorSpace 签名为 CMYK；
    /// 2) JPEG SOF 段的组件数为 4（CMYK / YCCK）。
    /// 说明：ImageSharp 4.x 仅暴露 ICC 签名与色彩空间，不提供描述文本，
    /// 故 iccName 恒为空，此处如实置 null，不臆造描述。
    /// </summary>
    static string DetectColorSpace(string path, ImageMetadata meta,
        out bool isCmyk, out string? iccName)
    {
        isCmyk = false;
        iccName = null;

        try
        {
            var space = meta.IccProfile?.Header.DataColorSpace.ToString();
            if (!string.IsNullOrEmpty(space))
            {
                if (space.Contains("Cmyk", StringComparison.OrdinalIgnoreCase)) isCmyk = true;
                // 保留签名文本作为可读标识（如 "Cmyk"）
                iccName = space;
            }
        }
        catch { /* 探测失败不影响主流程 */ }

        try
        {
            if (IsJpeg(path) && ProbeJpegForCmyk(path)) isCmyk = true;
        }
        catch { }

        return isCmyk ? "CMYK 印刷稿" : "RGB / sRGB";
    }

    static bool IsJpeg(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[3];
        return fs.Read(head) == 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
    }

    /// <summary>扫描 JPEG 段：APP14(0xEE) 的 Adobe transform，以及 SOF 组件数。</summary>
    static bool ProbeJpegForCmyk(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int i = 2; // 跳过 SOI
        bool adobe = false;
        int frameComponents = 0;

        while (i + 4 < data.Length)
        {
            if (data[i] != 0xFF) { i++; continue; }
            byte marker = data[i + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker == 0xD9 || marker == 0xDA) break; // 到达图像数据
            if (i + 4 > data.Length) break;
            int segLen = (data[i + 2] << 8) | data[i + 3];

            // APP14 Adobe：段体以 "Adobe" 开头
            if (marker == 0xEE && i + 9 < data.Length &&
                data[i + 4] == 'A' && data[i + 5] == 'd' && data[i + 6] == 'o' &&
                data[i + 7] == 'b' && data[i + 8] == 'e')
            {
                adobe = true;
                // transform 字节：0=未知(CMYK), 1=YCbCr, 2=YCCK
                int tIdx = i + 4 + 5 + 2 + 2; // "Adobe" + version(2) + flags0(2)
                if (tIdx < data.Length) { /* transform 值不影响判定，仅记录 adobe 标记 */ }
            }

            // SOF0..SOF15（排除 DHT C4/DAC CC/RST）
            bool isSof = (marker >= 0xC0 && marker <= 0xCF)
                         && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof && i + 10 < data.Length)
            {
                frameComponents = data[i + 9];
                break;
            }
            i += 2 + segLen;
        }

        // 4 组件 = CMYK/YCCK；Adobe 标记 + 4 组件可确认
        return frameComponents == 4 || (adobe && frameComponents == 4);
    }

    sealed record Cluster(byte R, byte G, byte B, int Count);

    /// <summary>
    /// 颜色量化：先按每通道 5bit 粗分桶取高频候选，
    /// 再用 Lab 空间 CIEDE2000 去重（ΔE &lt; 4 视为同色），避免近似色重复占位。
    /// </summary>
    static List<Cluster> Quantize(List<RgbSample> pixels, int maxColors)
    {
        var buckets = new Dictionary<int, List<RgbSample>>(4096);
        foreach (var p in pixels)
        {
            int key = ((p.R >> 3) << 10) | ((p.G >> 3) << 5) | (p.B >> 3);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new(64);
            list.Add(p);
        }

        var ordered = buckets.OrderByDescending(kv => kv.Value.Count).ToList();
        var picked = new List<Cluster>(maxColors);

        foreach (var kv in ordered)
        {
            if (picked.Count >= maxColors) break;
            var avg = AvgColor(kv.Value);
            var lab = ColorMath.RgbToLab(avg);
            bool dup = picked.Any(c =>
                ColorMath.DeltaE2000(lab, ColorMath.RgbToLab(new ColorMath.Rgb(c.R, c.G, c.B))) < 4.0);
            if (dup) continue;
            picked.Add(new Cluster(avg.R, avg.G, avg.B, kv.Value.Count));
        }

        if (picked.Count == 0 && ordered.Count > 0)
        {
            var a = AvgColor(ordered[0].Value);
            picked.Add(new Cluster(a.R, a.G, a.B, ordered[0].Value.Count));
        }
        return picked;
    }

    static ColorMath.Rgb AvgColor(List<RgbSample> list)
    {
        double r = 0, g = 0, b = 0;
        foreach (var p in list) { r += p.R; g += p.G; b += p.B; }
        int n = list.Count;
        return new ColorMath.Rgb(
            (byte)Math.Clamp(Math.Round(r / n), 0, 255),
            (byte)Math.Clamp(Math.Round(g / n), 0, 255),
            (byte)Math.Clamp(Math.Round(b / n), 0, 255));
    }
}
