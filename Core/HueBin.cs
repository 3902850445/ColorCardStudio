namespace ColorCardStudio.Core;

/// <summary>
/// 色相分箱：把任意颜色归入 13 类之一。
/// 这是色相分类的唯一定义处——前端通过 getStandards 拿到同一份 key，
/// 保证导入、筛选、内置色卡三处使用完全一致的分类口径。
/// </summary>
public static class HueBin
{
    /// <summary>分箱定义：(key, 中文名, 起始角度, 结束角度)。红色跨 0° 拆成两段。</summary>
    public static readonly (string Key, string Name, double Start, double End)[] Bins =
    {
        ("red",      "红色",   345, 360),
        ("red",      "红色",     0,  15),
        ("orange",   "橙色",    15,  45),
        ("amber",    "琥珀",    45,  70),
        ("yellow",   "黄色",    70, 100),
        ("lime",     "黄绿",   100, 140),
        ("green",    "绿色",   140, 170),
        ("emerald",  "翠绿",   170, 195),
        ("cyan",     "青色",   195, 220),
        ("sky",      "天蓝",   220, 245),
        ("blue",     "蓝色",   245, 270),
        ("violet",   "紫罗兰", 270, 295),
        ("magenta",  "洋红",   295, 345),
        ("gray",     "中性灰",   0,   0),   // 特殊：低饱和度归入
    };

    /// <summary>低饱和度阈值：低于此值视为中性灰。</summary>
    const double GraySaturation = 0.12;
    /// <summary>极暗阈值：低于此值也视为中性（避免极暗色被误判为彩色）。</summary>
    const double GrayValue = 0.10;

    /// <summary>把 HEX 颜色归入分箱 key。</summary>
    public static string FromHex(string hex)
    {
        try { return FromRgb(ColorMath.FromHex(hex)); }
        catch { return "gray"; }
    }

    /// <summary>把 RGB 颜色归入分箱 key。</summary>
    public static string FromRgb(ColorMath.Rgb rgb)
    {
        (double h, double s, double v) = ToHsv(rgb);
        if (s < GraySaturation || v < GrayValue) return "gray";

        foreach (var (key, _, start, end) in Bins)
        {
            if (key == "gray") continue;
            if (h >= start && h < end) return key;
        }
        return "gray";
    }

    /// <summary>色箱中文名。</summary>
    public static string NameOf(string key)
    {
        foreach (var (k, name, _, _) in Bins)
            if (k == key) return name;
        return key;
    }

    /// <summary>色箱代表色（用于前端圆点）。</summary>
    public static string SwatchOf(string key) => key switch
    {
        "red" => "#e5484d", "orange" => "#f76b15", "amber" => "#ffb224",
        "yellow" => "#f5d90a", "lime" => "#99d52a", "green" => "#30a46c",
        "emerald" => "#12a594", "cyan" => "#05a2c2", "sky" => "#0090ff",
        "blue" => "#3e63dd", "violet" => "#6e56cf", "magenta" => "#d6409f",
        _ => "#8b96a4",
    };

    static (double H, double S, double V) ToHsv(ColorMath.Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 1e-12)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max <= 0 ? 0 : d / max, max);
    }
}
