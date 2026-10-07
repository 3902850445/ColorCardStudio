namespace ColorCardStudio.Core;

/// <summary>
/// 印刷标准知识库。
/// 数据依据 ISO 12647-2、FOGRA 39/47/51、ECI、IDEAlliance GRACoL 等公开标准，
/// 每个标准给出：纸张白点、实地 CMYK 的目标 Lab、TAC 上限、可用 ΔE 容差。
/// 用于把屏幕 RGB 反算为「该标准下的合法 CMYK」，并对结果做合规校验。
/// </summary>
public sealed record PrintStandard(
    string Id,
    string Name,
    string PaperType,
    string Region,
    string Process,
    double TacLimit,           // 总墨量上限 %
    ColorMath.Lab PaperWhite,            // 纸张白点 Lab (D50/2°)
    ColorMath.Lab SolidCyan,             // C100 实地目标 Lab
    ColorMath.Lab SolidMagenta,
    ColorMath.Lab SolidYellow,
    ColorMath.Lab SolidBlack,
    double TolerableDeltaE,    // 平均可接受 ΔE
    double MaxDeltaE,          // 单点最大 ΔE
    string Note)
{
    /// <summary>总墨量合计（C+M+Y+K）。</summary>
    public static double TotalInk(ColorMath.Cmyk c) => c.C + c.M + c.Y + c.K;

    /// <summary>TAC 是否超标。</summary>
    public bool IsTacExceeded(ColorMath.Cmyk c) => TotalInk(c) > TacLimit + 1e-6;

    /// <summary>按此标准把 RGB 反算为 CMYK（带 TAC 限制）。</summary>
    public ColorMath.Cmyk ToCmyk(ColorMath.Rgb rgb) => ColorMath.RgbToCmyk(rgb, TacLimit);

    /// <summary>校验 CMYK 是否符合本标准，返回问题列表。</summary>
    public IReadOnlyList<string> Validate(ColorMath.Cmyk c)
    {
        var issues = new List<string>();
        double tac = TotalInk(c);
        if (tac > TacLimit + 1e-6)
            issues.Add($"总墨量 {tac:F0}% 超过 {Name} 的 {TacLimit:F0}% 上限");
        foreach (var (label, v) in new[] { ("C", c.C), ("M", c.M), ("Y", c.Y), ("K", c.K) })
            if (v is < -0.01 or > 100.01)
                issues.Add($"{label} 分量 {v:F1}% 超出 0-100% 范围");
        return issues;
    }
}

public static class PrintStandards
{
    // ISO 12647-2 铜版纸实地 Lab 参考值（FOGRA39 / 常见 ECI 实测值）
    private static readonly ColorMath.Lab W = new(95.0, 0.5, -3.5);      // 纸张白点
    private static readonly ColorMath.Lab SolC = new(55.0, -37.0, -50.0); // 青实地
    private static readonly ColorMath.Lab SolM = new(46.0, 74.0, -3.0);   // 品红实地
    private static readonly ColorMath.Lab SolY = new(89.0, -5.0, 93.0);   // 黄实地
    private static readonly ColorMath.Lab SolK = new(16.0, 0.0, 0.0);     // 黑实地

    public static readonly PrintStandard IsoCoatedV2 = new(
        "iso-coated-v2",
        "ISO Coated v2 (ECI)",
        "铜版纸 / 涂布纸（纸种 1、2）",
        "欧洲",
        "胶版印刷（单张纸）",
        330,
        W, SolC, SolM, SolY, SolK,
        1.5, 2.5,
        "最通用的国际标准，欧美胶版印刷默认；另有 300% 限墨变体");

    public static readonly PrintStandard IsoCoatedV2_300 = new(
        "iso-coated-v2-300",
        "ISO Coated v2 300%",
        "铜版纸 / 涂布纸",
        "欧洲",
        "轮转胶版印刷",
        300,
        W, SolC, SolM, SolY, SolK,
        1.5, 2.5,
        "ISO Coated v2 的 300% 限墨变体，用于干燥条件更严的轮转机");

    public static readonly PrintStandard PsoCoatedV3 = new(
        "pso-coated-v3",
        "PSO Coated v3 (FOGRA51)",
        "铜版纸 / 涂布纸",
        "欧洲",
        "胶版印刷（单张纸）",
        300,
        new ColorMath.Lab(95.0, 0.6, -3.0),
        new ColorMath.Lab(54.5, -36.0, -49.0),
        new ColorMath.Lab(46.5, 73.0, -2.5),
        new ColorMath.Lab(89.5, -4.5, 92.0),
        new ColorMath.Lab(15.5, 0.3, -0.5),
        1.5, 2.5,
        "ISO 12647-2:2013 新版标准，取代 Coated v2，含荧光增白纸特性");

    public static readonly PrintStandard PsoUncoated = new(
        "pso-uncoated",
        "PSO Uncoated (FOGRA47)",
        "非涂布纸 / 胶版纸",
        "欧洲",
        "胶版印刷（单张纸）",
        280,
        new ColorMath.Lab(96.0, 0.2, -4.5),
        new ColorMath.Lab(56.5, -35.0, -48.0),
        new ColorMath.Lab(48.0, 72.0, -2.0),
        new ColorMath.Lab(90.5, -4.0, 91.0),
        new ColorMath.Lab(17.5, 0.2, -0.8),
        2.0, 3.0,
        "非涂布纸吸墨强，限墨更严；纸张白点更高");

    public static readonly PrintStandard Gracol2013 = new(
        "gracol-2013",
        "GRACoL 2013 (CRPC6)",
        "商业胶版纸",
        "北美",
        "胶版印刷（单张纸）",
        300,
        new ColorMath.Lab(94.5, 1.2, -4.2),
        new ColorMath.Lab(55.5, -38.0, -51.0),
        new ColorMath.Lab(46.0, 75.5, -4.0),
        new ColorMath.Lab(89.5, -6.0, 94.0),
        new ColorMath.Lab(16.5, 0.5, -1.2),
        2.0, 3.5,
        "北美 CGATS TR 006 体系，IDEAlliance 维护");

    public static readonly PrintStandard Swop2006 = new(
        "swop-2006",
        "SWOP 2006 Coated v2",
        "商业涂布纸",
        "北美",
        "胶版印刷（单张纸）",
        300,
        new ColorMath.Lab(94.0, 1.5, -5.0),
        new ColorMath.Lab(55.0, -39.0, -52.0),
        new ColorMath.Lab(45.5, 76.0, -5.0),
        new ColorMath.Lab(89.0, -6.5, 95.0),
        new ColorMath.Lab(17.0, 0.8, -1.5),
        2.0, 3.5,
        "SWOP 色域较窄，鲜亮色饱和度低");

    public static readonly PrintStandard JapanColor2011 = new(
        "japan-color-2011",
        "Japan Color 2011 Coated",
        "涂布纸",
        "日本",
        "胶版印刷（单张纸）",
        320,
        new ColorMath.Lab(95.5, 0.3, -2.5),
        new ColorMath.Lab(55.5, -36.5, -49.5),
        new ColorMath.Lab(47.0, 73.5, -2.0),
        new ColorMath.Lab(90.0, -4.0, 92.5),
        new ColorMath.Lab(16.0, 0.2, -0.3),
        1.5, 2.5,
        "日本国标准（Japan Printing），涂布纸白点偏高、略偏暖");

    public static readonly PrintStandard Fogra39Uncoated = new(
        "fogra39-uncoated",
        "FOGRA39 Uncoated",
        "非涂布纸",
        "欧洲",
        "胶版印刷（单张纸）",
        300,
        new ColorMath.Lab(96.0, 0.3, -4.0),
        new ColorMath.Lab(56.0, -36.0, -49.0),
        new ColorMath.Lab(47.5, 73.0, -2.5),
        new ColorMath.Lab(90.0, -5.0, 92.0),
        new ColorMath.Lab(17.0, 0.4, -1.0),
        2.0, 3.0,
        "FOGRA39 特性数据非涂布纸版本");

    public static readonly PrintStandard CgatsTr001 = new(
        "cgats-tr-001",
        "CGATS TR 001 (ISO 12647-1)",
        "数码印刷（不指定纸张）",
        "国际",
        "数码印刷 / 喷墨",
        300,
        new ColorMath.Lab(95.0, 1.0, -3.0),
        new ColorMath.Lab(54.0, -36.0, -48.0),
        new ColorMath.Lab(47.5, 71.0, -3.0),
        new ColorMath.Lab(89.0, -6.0, 90.0),
        new ColorMath.Lab(17.5, 0.8, -1.5),
        2.0, 3.5,
        "ISO 12647-1 数码印刷标准，色域宽于胶版");

    /// <summary>内置标准（含 Web/RGB 屏幕模式）。</summary>
    public static readonly IReadOnlyList<PrintStandard> All = new[]
    {
        PsoCoatedV3,
        IsoCoatedV2,
        IsoCoatedV2_300,
        PsoUncoated,
        Gracol2013,
        Swop2006,
        JapanColor2011,
        Fogra39Uncoated,
        CgatsTr001,
    };

    public static PrintStandard ById(string? id) =>
        All.FirstOrDefault(s => s.Id == id) ?? PsoCoatedV3;
}
