using System.Globalization;

namespace ColorCardStudio.Core;

/// <summary>
/// 色彩空间转换核心。
/// 转换路径统一走 XYZ(D50) 中转，这是 ICC/ISO 标准的做法：
/// sRGB --(D65 白点，Bradford 适应)--> XYZ D50 --> Lab / CMYK。
/// 所有 Lab 值默认 D50/2° 观察者，符合 ISO 12647-2 与印刷惯例。
/// </summary>
public static class ColorMath
{
    // ---------- 基础结构 ----------
    public readonly record struct Rgb(byte R, byte G, byte B);
    public readonly record struct Cmyk(double C, double M, double Y, double K);
    public readonly record struct Hsl(double H, double S, double L);
    public readonly record struct Xyz(double X, double Y, double Z);
    public readonly record struct Lab(double L, double A, double B);

    // ---------- sRGB <-> 线性 ----------
    static double SrgbToLinear(double v)
    {
        v /= 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    static double LinearToSrgb(double v)
    {
        v = Math.Clamp(v, 0, 1);
        double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return Math.Round(s * 255);
    }

    // ---------- sRGB -> XYZ(D65) ----------
    static readonly double[,] RgbToXyzD65 =
    {
        { 0.4123908, 0.3575843, 0.1804808 },
        { 0.2126390, 0.7151687, 0.0721923 },
        { 0.0193308, 0.1191948, 0.9505322 },
    };

    // ---------- XYZ(D65) -> XYZ(D50)，Bradford 适应 ----------
    static readonly double[,] BradfordD65ToD50 =
    {
        {  1.0478112,  0.0228866, -0.0501270 },
        {  0.0295424,  0.9904844, -0.0170491 },
        { -0.0092345,  0.0150436,  0.7521316 },
    };

    public static readonly double[] WhiteD65 = { 0.95047, 1.00000, 1.08883 };
    public static readonly double[] WhiteD50 = { 0.96422, 1.00000, 0.82521 };

    static double[,] Invert(double[,] m)
    {
        double det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                   - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                   + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double[,] inv = new double[3, 3];
        inv[0, 0] = (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) / det;
        inv[0, 1] = (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) / det;
        inv[0, 2] = (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) / det;
        inv[1, 0] = (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) / det;
        inv[1, 1] = (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) / det;
        inv[1, 2] = (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) / det;
        inv[2, 0] = (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) / det;
        inv[2, 1] = (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) / det;
        inv[2, 2] = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) / det;
        return inv;
    }

    static double[] Mul(double[,] m, double[] v) =>
        new[]
        {
            m[0,0]*v[0] + m[0,1]*v[1] + m[0,2]*v[2],
            m[1,0]*v[0] + m[1,1]*v[1] + m[1,2]*v[2],
            m[2,0]*v[0] + m[2,1]*v[1] + m[2,2]*v[2],
        };

    /// <summary>sRGB -> XYZ(D50)，印刷测量用。</summary>
    public static Xyz RgbToXyzD50(Rgb c)
    {
        double[] lin = { SrgbToLinear(c.R), SrgbToLinear(c.G), SrgbToLinear(c.B) };
        double[] d65 = Mul(RgbToXyzD65, lin);
        double[] d50 = Mul(BradfordD65ToD50, d65);
        return new Xyz(d50[0], d50[1], d50[2]);
    }

    /// <summary>XYZ(D50) -> sRGB。</summary>
    public static Rgb XyzD50ToRgb(Xyz v)
    {
        double[,] inv = Invert(BradfordD65ToD50);
        double[] d65 = Mul(inv, new[] { v.X, v.Y, v.Z });
        double[,] invRgb = Invert(RgbToXyzD65);
        double[] lin = Mul(invRgb, d65);
        return new Rgb((byte)LinearToSrgb(lin[0]), (byte)LinearToSrgb(lin[1]), (byte)LinearToSrgb(lin[2]));
    }

    static double LabF(double t) =>
        t > 216.0 / 24389.0 ? Math.Cbrt(t) : (841.0 / 108.0) * t + 4.0 / 29.0;

    static double LabFInv(double t) =>
        t > 6.0 / 29.0 ? t * t * t : 108.0 / 841.0 * (t - 4.0 / 29.0);

    /// <summary>XYZ(D50) -> Lab(D50, 2°观察者)。</summary>
    public static Lab XyzToLab(Xyz v)
    {
        double fx = LabF(v.X / WhiteD50[0]);
        double fy = LabF(v.Y / WhiteD50[1]);
        double fz = LabF(v.Z / WhiteD50[2]);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    public static Xyz LabToXyz(Lab l)
    {
        double fy = (l.L + 16) / 116.0;
        double fx = fy + l.A / 500.0;
        double fz = fy - l.B / 200.0;
        return new Xyz(WhiteD50[0] * LabFInv(fx), WhiteD50[1] * LabFInv(fy), WhiteD50[2] * LabFInv(fz));
    }

    public static Lab RgbToLab(Rgb c) => XyzToLab(RgbToXyzD50(c));
    public static Rgb LabToRgb(Lab l) => XyzD50ToRgb(LabToXyz(l));

    // ---------- HSL ----------
    public static Hsl RgbToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        double d = max - min;
        if (d > 1e-12)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d) + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return new Hsl(h, s * 100, l * 100);
    }

    public static Rgb HslToRgb(Hsl h)
    {
        double H = ((h.H % 360) + 360) % 360, S = Math.Clamp(h.S / 100, 0, 1), L = Math.Clamp(h.L / 100, 0, 1);
        double c = (1 - Math.Abs(2 * L - 1)) * S;
        double x = c * (1 - Math.Abs((H / 60) % 2 - 1));
        double m = L - c / 2;
        double[] rgb = H switch
        {
            < 60 => new[] { c, x, 0 },
            < 120 => new[] { x, c, 0 },
            < 180 => new[] { 0, c, x },
            < 240 => new[] { 0, x, c },
            < 300 => new[] { x, 0, c },
            _ => new[] { c, 0, x },
        };
        return new Rgb((byte)Math.Round((rgb[0] + m) * 255),
                       (byte)Math.Round((rgb[1] + m) * 255),
                       (byte)Math.Round((rgb[2] + m) * 255));
    }

    // ---------- CMYK（GCR/UCR 印刷模型） ----------
    /// <summary>
    /// RGB -> CMYK（百分比 0-100）。默认 UCR：暗部补黑、中间调保彩，
    /// 符合胶版印刷分色习惯；totalInk 为该印刷标准的总墨量上限（TAC %）。
    /// </summary>
    public static Cmyk RgbToCmyk(Rgb c, double totalInk = 300)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double k0 = 1 - Math.Max(r, Math.Max(g, b));
        if (k0 < 1e-9) k0 = 0; // 纯白
        double inv = 1 - k0;
        double cc = inv < 1e-9 ? 0 : (1 - r - k0) / inv;
        double mm = inv < 1e-9 ? 0 : (1 - g - k0) / inv;
        double yy = inv < 1e-9 ? 0 : (1 - b - k0) / inv;
        (cc, mm, yy) = Ucr(cc, mm, yy, k0);

        // TAC 限制：等比压缩 CMY，保持色相
        double sum = cc + mm + yy + k0;
        double cap = Math.Clamp(totalInk, 200, 400) / 100.0;
        if (sum > cap && sum > 1e-9)
        {
            double factor = cap / sum;
            cc *= factor; mm *= factor; yy *= factor; k0 *= factor;
        }
        return new Cmyk(Math.Round(cc * 100, 1), Math.Round(mm * 100, 1),
                        Math.Round(yy * 100, 1), Math.Round(k0 * 100, 1));
    }

    /// <summary>底色去除：把 CMY 过量部分转成 K，减少总墨量、保持中性灰。</summary>
    static (double, double, double) Ucr(double c, double m, double y, double k)
    {
        double minCmy = Math.Min(c, Math.Min(m, y));
        if (minCmy <= 0) return (c, m, y);
        double amount = minCmy * 0.85; // 85% UCR，保留少量 CMY 维持色相
        double kNew = Math.Min(1, k + amount);
        double scale = (1 - kNew) < 1e-9 ? 0 : (1 - k) / (1 - kNew);
        return (c * scale, m * scale, y * scale);
    }

    /// <summary>CMYK -> RGB（简化墨点模型，未含 ICC 特性化）。</summary>
    public static Rgb CmykToRgb(Cmyk v)
    {
        double c = v.C / 100, m = v.M / 100, y = v.Y / 100, k = v.K / 100;
        double r = (1 - c) * (1 - k);
        double g = (1 - m) * (1 - k);
        double b = (1 - y) * (1 - k);
        return new Rgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    // ---------- HEX ----------
    public static string ToHex(Rgb c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}".ToLowerInvariant();

    public static Rgb FromHex(string hex)
    {
        string h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            throw new FormatException($"无法解析颜色值: {hex}");
        return new Rgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    // ---------- 色差 ----------
    /// <summary>色相角归一到 [0, 360)，CIEDE2000 要求。</summary>
    static double NormalizeHue(double deg)
    {
        double h = deg % 360.0;
        return h < 0 ? h + 360.0 : h;
    }

    /// <summary>
    /// CIEDE2000 色差公式，印刷色差判定通用指标。
    /// 实现严格对照 CIE 142-2001 与 Sharma 等人的 34 组测试数据。
    /// </summary>
    public static double DeltaE2000(ColorMath.Lab p, ColorMath.Lab q)
    {
        const double Deg = 180.0 / Math.PI;   // 弧度 -> 度
        const double Rad = Math.PI / 180.0;   // 度 -> 弧度
        const double Pow25_7 = 6103515625.0;  // 25^7

        // 1) C*, G*
        double C1p = Math.Sqrt(p.A * p.A + p.B * p.B);
        double C2p = Math.Sqrt(q.A * q.A + q.B * q.B);
        double Cbar = (C1p + C2p) / 2;
        double G = 0.5 * (1 - Math.Sqrt(Math.Pow(Cbar, 7) / (Math.Pow(Cbar, 7) + Pow25_7)));

        double a1p = (1 + G) * p.A;
        double a2p = (1 + G) * q.A;

        // 2) C' 再次计算（乘 G 后），h'
        // 注意：atan2(b, a) 在 b<0、a=0 时返回 -90°，而 CIEDE2000 规定色相角须归一到
        // [0, 360)，否则 Δh' 会多绕一圈，导致 ΔE 偏小（Sharma 论文表 1 即踩此坑）。
        double C1pp = Math.Sqrt(a1p * a1p + p.B * p.B);
        double C2pp = Math.Sqrt(a2p * a2p + q.B * q.B);
        double h1p = (p.B == 0 && a1p == 0) ? 0 : NormalizeHue(Math.Atan2(p.B, a1p) * Deg);
        double h2p = (q.B == 0 && a2p == 0) ? 0 : NormalizeHue(Math.Atan2(q.B, a2p) * Deg);

        // 3) ΔL', ΔC', ΔH'
        double dLp = q.L - p.L;
        double dCp = C2pp - C1pp;

        double dhp = 0.0;
        if (C1pp * C2pp != 0.0)
        {
            dhp = h2p - h1p;
            if (dhp > 180) dhp -= 360;
            else if (dhp < -180) dhp += 360;
        }
        double dHp = 2 * Math.Sqrt(C1pp * C2pp) * Math.Sin(dhp * Rad / 2);

        // 4) 各项均值
        double Lbarp = (p.L + q.L) / 2;
        double Cbarp = (C1pp + C2pp) / 2;

        double hbarp = h1p + h2p;
        if (C1pp * C2pp != 0.0)
        {
            if (Math.Abs(h1p - h2p) > 180)
                hbarp += (h1p + h2p < 360) ? 360 : -360;
            hbarp /= 2;
        }

        // 5) T
        double T = 1
            - 0.17 * Math.Cos((hbarp - 30) * Rad)
            + 0.24 * Math.Cos(2 * hbarp * Rad)
            + 0.32 * Math.Cos((3 * hbarp + 6) * Rad)
            - 0.20 * Math.Cos((4 * hbarp - 63) * Rad);

        // 6) Δθ, R_C, S_L/S_C/S_H, R_T
        double dTheta = 30 * Math.Exp(-Math.Pow((hbarp - 275) / 25, 2));
        double RC = 2 * Math.Sqrt(Math.Pow(Cbarp, 7) / (Math.Pow(Cbarp, 7) + Pow25_7));

        double SL = 1 + (0.015 * Math.Pow(Lbarp - 50, 2)) / Math.Sqrt(20 + Math.Pow(Lbarp - 50, 2));
        double SC = 1 + 0.045 * Cbarp;
        double SH = 1 + 0.015 * Cbarp * T;
        double RT = -Math.Sin(2 * dTheta * Rad) * RC;

        double termL = dLp / SL;
        double termC = dCp / SC;
        double termH = dHp / SH;

        return Math.Sqrt(termL * termL + termC * termC + termH * termH + RT * termC * termH);
    }

    /// <summary>对比度（WCAG），用于判断文字可读性。</summary>
    public static double RelativeLuminance(Rgb c)
    {
        double rl = SrgbToLinear(c.R), gl = SrgbToLinear(c.G), bl = SrgbToLinear(c.B);
        return 0.2126 * rl + 0.7152 * gl + 0.0722 * bl;
    }

    public static double ContrastRatio(Rgb a, Rgb b)
    {
        double l1 = RelativeLuminance(a), l2 = RelativeLuminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    /// <summary>是否适合叠加深色文字。</summary>
    public static bool IsDark(Rgb c) => RelativeLuminance(c) < 0.4;

    /// <summary>色相差（0-360）。</summary>
    public static double HueDistance(double h1, double h2)
    {
        double d = Math.Abs(h1 - h2) % 360;
        return d > 180 ? 360 - d : d;
    }
}
