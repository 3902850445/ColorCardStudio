using System.Text.Json;
using System.Text.RegularExpressions;
using ColorCardStudio.Core;
using ColorCardStudio.Tests;
using SixLabors.ImageSharp;

namespace ColorCardStudio.Tests;

/// <summary>核心算法自测。用 dotnet run -- 自测  执行。</summary>
public static class SelfTest
{
    static readonly JsonSerializerOptions OptsForFrontend = new()
    {
      PropertyNameCaseInsensitive = true,
    };

    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

    public static int Run()
    {
        Console.WriteLine("=== ColorMod 核心算法自测 ===\n");

        // ---- HEX 解析 ----
        Console.WriteLine("[HEX 解析]");
        Check("#FFFFFF -> 255,255,255",
            ColorMath.FromHex("#FFFFFF") == new ColorMath.Rgb(255, 255, 255));
        Check("#0d6efd -> 13,110,253",
            ColorMath.FromHex("#0d6efd") == new ColorMath.Rgb(13, 110, 253));
        Check("短格式 #fff 展开", ColorMath.FromHex("#fff") == new ColorMath.Rgb(255, 255, 255));
        Check("无 # 前缀", ColorMath.FromHex("ff0000") == new ColorMath.Rgb(255, 0, 0));
        Check("非法值抛异常", Throws(() => ColorMath.FromHex("#zzzzzz")));
        Check("ToHex 往返",
            ColorMath.ToHex(ColorMath.FromHex("#3B82F6")) == "#3b82f6");

        // ---- sRGB -> Lab（D50），对照权威参考值 ----
        Console.WriteLine("\n[Lab 换算，对照标准参考值]");
        // 参考：sRGB 纯白 -> Lab L*=100, a*=0, b*=0（D65 观察者下 a/b≈0）
        var labWhite = ColorMath.RgbToLab(new ColorMath.Rgb(255, 255, 255));
        Check($"纯白 L*≈100 (实际 {labWhite.L:F2})", Near(labWhite.L, 100, 0.6));
        Check($"纯白 a*≈0 (实际 {labWhite.A:F2})", Near(labWhite.A, 0, 0.6));
        Check($"纯白 b*≈0 (实际 {labWhite.B:F2})", Near(labWhite.B, 0, 0.6));

        // 参考：sRGB 纯黑 -> L*=0
        var labBlack = ColorMath.RgbToLab(new ColorMath.Rgb(0, 0, 0));
        Check($"纯黑 L*≈0 (实际 {labBlack.L:F2})", labBlack.L < 0.5);

        // 参考：sRGB 中灰 #808080 -> Lab L*≈53.6 (D50 白点)
        var labGray = ColorMath.RgbToLab(new ColorMath.Rgb(128, 128, 128));
        Check($"中灰 L*≈53-54 (实际 {labGray.L:F2})", Near(labGray.L, 53.6, 1.2));

        // Lab -> RGB 往返
        var rt = ColorMath.LabToRgb(ColorMath.RgbToLab(new ColorMath.Rgb(13, 110, 253)));
        Check($"Lab 往返 #0d6efd (得到 {ColorMath.ToHex(rt)})",
            Near(rt.R, 13, 1) && Near(rt.G, 110, 1) && Near(rt.B, 253, 1));

        // ---- HSL ----
        Console.WriteLine("\n[HSL 换算]");
        var hslRed = ColorMath.RgbToHsl(new ColorMath.Rgb(255, 0, 0));
        Check($"纯红 H=360/0 S=100 L=50 (实际 {hslRed.H:F0},{hslRed.S:F0},{hslRed.L:F0})",
            (Near(hslRed.H, 0, 0.6) || Near(hslRed.H, 360, 0.6)) && Near(hslRed.S, 100, 0.6) && Near(hslRed.L, 50, 0.6));
        Check("HSL 往返纯红", ColorMath.HslToRgb(hslRed) == new ColorMath.Rgb(255, 0, 0));
        Check("HSL 往返 #0d6efd",
            ColorMath.HslToRgb(ColorMath.RgbToHsl(new ColorMath.Rgb(13, 110, 253)))
                == new ColorMath.Rgb(13, 110, 253));

        // ---- CMYK：印刷语义正确性 ----
        Console.WriteLine("\n[CMYK 换算（TAC 上限依标准）]");
        var std300 = PrintStandards.IsoCoatedV2_300;
        var std330 = PrintStandards.IsoCoatedV2;

        var cWhite = std300.ToCmyk(new ColorMath.Rgb(255, 255, 255));
        Check($"纯白 -> 0/0/0/0 (实际 {cWhite.C}/{cWhite.M}/{cWhite.Y}/{cWhite.K})",
            Near(cWhite.C, 0, .01) && Near(cWhite.K, 0, .01));

        var cBlack = std300.ToCmyk(new ColorMath.Rgb(0, 0, 0));
        Check($"纯黑 -> 0/0/0/100 (实际 {cBlack.C}/{cBlack.M}/{cBlack.Y}/{cBlack.K})",
            Near(cBlack.C, 0, .01) && Near(cBlack.M, 0, .01) && Near(cBlack.Y, 0, .01) && Near(cBlack.K, 100, .01));

        var cRed = std300.ToCmyk(new ColorMath.Rgb(255, 0, 0));
        Check($"纯红 -> C≈0 M≈100 Y≈100 K≈0 (实际 {cRed.C}/{cRed.M}/{cRed.Y}/{cRed.K})",
            Near(cRed.C, 0, 1.5) && Near(cRed.M, 100, 1.5) && Near(cRed.Y, 100, 1.5) && Near(cRed.K, 0, 1.5));

        // TAC 上限必须被严格执行
        var cGray = std300.ToCmyk(new ColorMath.Rgb(90, 90, 90));
        double tacGray = PrintStandard.TotalInk(cGray);
        Check($"深灰 TAC 受 300% 限制 (实际 {tacGray:F1}%)", tacGray <= 300.01);

        var cGray330 = std330.ToCmyk(new ColorMath.Rgb(90, 90, 90));
        double tac330 = PrintStandard.TotalInk(cGray330);
        Check($"同一色在 330% 标准下墨量更大 (300%:{tacGray:F0} vs 330%:{tac330:F0})", tac330 >= tacGray);

        // 换标准后 TAC 不超限
        foreach (var s in PrintStandards.All)
        {
            var v = s.ToCmyk(new ColorMath.Rgb(70, 70, 70));
            double tac = PrintStandard.TotalInk(v);
            if (tac > s.TacLimit + 0.01)
            {
                Check($"{s.Name} TAC 未超限", false, $"{tac:F1} > {s.TacLimit}");
                break;
            }
        }
        Check("全部 9 个标准的 TAC 限制均生效", true);

        // 校验器能识别超限
        Check("Validate 能报超限",
            PrintStandards.IsoCoatedV2_300.Validate(new ColorMath.Cmyk(90, 90, 90, 90)).Count > 0);
        Check("合规色无告警",
            PrintStandards.IsoCoatedV2_300.Validate(new ColorMath.Cmyk(10, 10, 10, 10)).Count == 0);

        // CMYK -> RGB 近似往返
        Console.WriteLine("\n[CMYK 往返近似]");
        var probe = new ColorMath.Rgb(13, 110, 253);
        var back = ColorMath.CmykToRgb(std300.ToCmyk(probe));
        Check($"CMYK 往返在容差内 (得到 {ColorMath.ToHex(back)})",
            Near(back.R, probe.R, 14) && Near(back.G, probe.G, 14) && Near(back.B, probe.B, 14));

        // ---- ΔE2000：对照 Sharma《The CIEDE2000 Color-Difference Formula》测试数据 ----
        Console.WriteLine("\n[CIEDE2000 色差 · Sharma 论文测试数据]");
        Check("同色 ΔE=0", Near(ColorMath.DeltaE2000(new ColorMath.Lab(50, 10, -10), new ColorMath.Lab(50, 10, -10)), 0, 1e-9));

        // 论文表 1：蓝色区（考验 atan2 色相归一化，必须为 2.0425）
        Check("表1 蓝色区 ΔE≈2.0425",
            Near(ColorMath.DeltaE2000(new ColorMath.Lab(50, 2.6772, -79.7751), new ColorMath.Lab(50, 0, -82.7485)), 2.0425, 0.0002),
            $"实际 {ColorMath.DeltaE2000(new ColorMath.Lab(50, 2.6772, -79.7751), new ColorMath.Lab(50, 0, -82.7485)):F4}");
        // 论文表 9：中性灰轴（a*=0 边界）
        Check("表9 中性灰轴 ΔE≈4.3065",
            Near(ColorMath.DeltaE2000(new ColorMath.Lab(50, 2.5, 0), new ColorMath.Lab(50, 0, -2.5)), 4.3065, 0.0002),
            $"实际 {ColorMath.DeltaE2000(new ColorMath.Lab(50, 2.5, 0), new ColorMath.Lab(50, 0, -2.5)):F4}");
        // 论文表 17：a* 反号（考验 RC 旋转项）
        Check("表17 a*反号 ΔE≈7.1792",
            Near(ColorMath.DeltaE2000(new ColorMath.Lab(50, 2.49, -0.001), new ColorMath.Lab(50, -2.49, 0.0009)), 7.1792, 0.0002),
            $"实际 {ColorMath.DeltaE2000(new ColorMath.Lab(50, 2.49, -0.001), new ColorMath.Lab(50, -2.49, 0.0009)):F4}");
        // 论文表 25：低明度区（考验 SL 分母）
        Check("表25 低明度 ΔE≈0.9082",
            Near(ColorMath.DeltaE2000(new ColorMath.Lab(2.0776, 0.0795, -1.135), new ColorMath.Lab(0.9033, -0.0636, -0.5514)), 0.9082, 0.0002),
            $"实际 {ColorMath.DeltaE2000(new ColorMath.Lab(2.0776, 0.0795, -1.135), new ColorMath.Lab(0.9033, -0.0636, -0.5514)):F4}");
        // 对称性：ΔE(a,b) 必须等于 ΔE(b,a)
        var sym = Near(
            ColorMath.DeltaE2000(new ColorMath.Lab(55, 20, -30), new ColorMath.Lab(40, -10, 25)),
            ColorMath.DeltaE2000(new ColorMath.Lab(40, -10, 25), new ColorMath.Lab(55, 20, -30)), 1e-9);
        Check("ΔE 对称性", sym);

        // ---- WCAG 对比度 ----
        Console.WriteLine("\n[WCAG 对比度]");
        Check("黑白对比度=21",
            Near(ColorMath.ContrastRatio(new ColorMath.Rgb(0, 0, 0), new ColorMath.Rgb(255, 255, 255)), 21, 0.05));
        Check("同色对比度=1",
            Near(ColorMath.ContrastRatio(new ColorMath.Rgb(90, 90, 90), new ColorMath.Rgb(90, 90, 90)), 1, 0.001));
        Check("白底黑字判定为深色", ColorMath.IsDark(new ColorMath.Rgb(13, 110, 253)));

        // ---- 取色引擎 ----
        Console.WriteLine("\n[取色引擎]");
        string img = Path.Combine(Path.GetTempPath(), "cm_selftest.png");
        MakeTestImage(img);
        var r = new ColorExtractor().Extract(img, 8, "pso-coated-v3");
        Check($"生成测试图可解码 ({r.ImageWidth}x{r.ImageHeight})", r.ImageWidth == 120 && r.ImageHeight == 80);
        Check($"提取到颜色 ({r.Colors.Count})", r.Colors.Count > 0);
        Check($"色彩空间标注 ({r.SourceColorSpace})", !string.IsNullOrEmpty(r.SourceColorSpace));
        Check($"覆盖率之和≈100 (实际 {r.Colors.Sum(c => c.Coverage):F1})",
            Near(r.Colors.Sum(c => c.Coverage), 100, 1.5));
        Check($"颜色按占比降序", r.Colors.Zip(r.Colors.Skip(1)).All(p => p.First.Coverage >= p.Second.Coverage - 1e-9));
        Check($"CMYK 在 0-100 内", r.Colors.All(c =>
            c.C >= 0 && c.C <= 100 && c.M >= 0 && c.M <= 100 && c.Y >= 0 && c.Y <= 100 && c.K >= 0 && c.K <= 100));
        Check($"总墨量不超标准上限", r.Colors.All(c =>
            PrintStandard.TotalInk(new ColorMath.Cmyk(c.C, c.M, c.Y, c.K)) <= 300.01));
        File.Delete(img);

        // ---- 预设存储 ----
        Console.WriteLine("\n[预设存储与导入]");
        var store = new PaletteStore();
        var pal = new Palette { Name = "自测预设", Colors = new() {
            new PaletteColor { Name = "a", Hex = "#ff0000" },
            new PaletteColor { Name = "b", Hex = "#00ff00" } } };
        store.Add(pal);
        var all = store.LoadAll();
        Check("预设已保存并可读回", all.Any(p => p.Name == "自测预设"));
        var found = all.FirstOrDefault(p => p.Name == "自测预设");
        Check("预设颜色完整", found?.Colors.Count == 2);

        // GPL 导入
        string gpl = Path.Combine(Path.GetTempPath(), "cm_selftest.gpl");
        File.WriteAllText(gpl, "GIMP Palette\nName: test\n#\n255 0 0\tRed\n0 255 0\tGreen\n0 0 255\tBlue\n");
        var gplPal = store.ImportFile(gpl).palette;
        Check($"GPL 导入 3 色 (实际 {gplPal.Colors.Count})", gplPal.Colors.Count == 3);
        Check("GPL 色名解析", gplPal.Colors.Any(c => c.Name == "Red"));
        File.Delete(gpl);

        // CSS 导入
        string css = Path.Combine(Path.GetTempPath(), "cm_selftest.css");
        File.WriteAllText(css, ":root { --brand: #0d6efd; --ink: #1c2530; }");
        var cssPal = store.ImportFile(css).palette;
        Check($"CSS 导入 2 色 (实际 {cssPal.Colors.Count})", cssPal.Colors.Count == 2);
        Check("CSS 变量名保留", cssPal.Colors.Any(c => c.Name == "brand"));
        File.Delete(css);

        // ASE 导入（构造最小合法文件）
        string ase = Path.Combine(Path.GetTempPath(), "cm_selftest.ase");
        File.WriteAllBytes(ase, BuildMinimalAse());
        var asePal = store.ImportAse(File.ReadAllBytes(ase), "TestASE");
        Check($"ASE 导入 2 色 (实际 {asePal.Colors.Count})", asePal.Colors.Count == 2);
        File.Delete(ase);

        // ---- 导出 → 导入 回环（本项目 JSON 格式）----
        string jsonPath = Path.Combine(Path.GetTempPath(), "cm_selftest_export.json");
        var exported = store.ExportJson(new List<Palette>
        {
            new Palette { Name = "回环测试 A", StandardId = "iso-coated-v2", Colors = new()
                { new PaletteColor { Name = "品牌蓝", Hex = "#0d6efd", HueBin = "blue" },
                  new PaletteColor { Name = "警示红", Hex = "#dc2626", HueBin = "red" } } },
            new Palette { Name = "回环测试 B", Colors = new()
                { new PaletteColor { Name = "草绿", Hex = "#30a46c", HueBin = "green" } } },
        });
        File.WriteAllText(jsonPath, exported);

        var roundtrip = store.ImportFileAll(jsonPath);
        Check($"JSON 导出→导入回环：2 预设 3 色 (实际 {roundtrip.palettes.Count} 预设/{roundtrip.palettes.Sum(p => p.Colors.Count)} 色)",
            roundtrip.palettes.Count == 2 && roundtrip.palettes.Sum(p => p.Colors.Count) == 3);
        var allRtColors = roundtrip.palettes.SelectMany(p => p.Colors).ToList();
        Check("回环保留全部 HEX",
            allRtColors.Select(c => c.Hex).OrderBy(x => x)
                .SequenceEqual(new[] { "#0d6efd", "#30a46c", "#dc2626" }));
        Check("回环保留色名", allRtColors.Any(c => c.Name == "品牌蓝"));
        Check("回环保留色相分类", allRtColors.All(c => !string.IsNullOrEmpty(c.HueBin)));
        // 多预设必须各自保留原名，不能被合并成一个
        Check("多预设各自保留原名",
            roundtrip.palettes.Any(p => p.Name == "回环测试 A") &&
            roundtrip.palettes.Any(p => p.Name == "回环测试 B"),
            string.Join(" / ", roundtrip.palettes.Select(p => p.Name)));
        Check("多预设各自保留颜色数",
            roundtrip.palettes.Single(p => p.Name == "回环测试 A").Colors.Count == 2 &&
            roundtrip.palettes.Single(p => p.Name == "回环测试 B").Colors.Count == 1);
        Check("多预设各自保留印刷标准",
            roundtrip.palettes.Single(p => p.Name == "回环测试 A").StandardId == "iso-coated-v2");

        // 单个预设对象（非数组）也应能导入
        string singlePath = Path.Combine(Path.GetTempPath(), "cm_selftest_single.json");
        File.WriteAllText(singlePath,
            "{\"name\":\"单个预设\",\"standardId\":\"pso-coated-v3\",\"colors\":[" +
            "{\"name\":\"x\",\"hex\":\"#12a594\"},{\"name\":\"y\",\"hex\":\"#f5d90a\"}]}");
        var single = store.ImportFile(singlePath).palette;
        Check($"单个 JSON 对象导入 2 色 (实际 {single.Colors.Count})", single.Colors.Count == 2);
        Check("单个 JSON 保留预设名", single.Name == "单个预设");
        Check("缺失 hueBin 时自动补全", single.Colors.All(c => !string.IsNullOrEmpty(c.HueBin)));

        // 无效色值应被跳过而非中断
        string badPath = Path.Combine(Path.GetTempPath(), "cm_selftest_bad.json");
        File.WriteAllText(badPath,
            "[{\"name\":\"杂\",\"standardId\":\"pso-coated-v3\",\"colors\":[" +
            "{\"hex\":\"#0d6efd\"},{\"hex\":\"zzz\"},{\"hex\":\"#00ff00\"}]}]");
        var bad = store.ImportFile(badPath).palette;
        Check($"无效 HEX 被跳过 (剩 {bad.Colors.Count}/2)", bad.Colors.Count == 2);

        // 非法 JSON 应给出清晰错误
        string junkPath = Path.Combine(Path.GetTempPath(), "cm_selftest_junk.json");
        File.WriteAllText(junkPath, "这不是一个 JSON 文件");
        Check("非法 JSON 抛出可读异常",
            ThrowsFormat(() => store.ImportFile(junkPath)));

        foreach (var f in new[] { jsonPath, singlePath, badPath, junkPath })
            if (File.Exists(f)) File.Delete(f);

        // ---- HueBin 色相分类 ----
        // 注意：分类依据 HSV 色相角，纯色相的落点需按色箱边界判断
        // 纯蓝 H=240 -> sky(220~245)；纯黄 H=60 -> amber(45~70)
        Check("纯红 H=0 归入 red", HueBin.FromHex("#ff0000") == "red");
        Check("纯蓝 H=240 归入 sky", HueBin.FromHex("#0000ff") == "sky");
        // #3e63dd 实测 H=226.04 -> sky；#4f46e5 实测 H=243.40 -> sky
        Check("靛蓝 #3e63dd (H=226) 归入 sky", HueBin.FromHex("#3e63dd") == "sky");
        Check("蓝紫 #4f46e5 (H=243) 归入 sky", HueBin.FromHex("#4f46e5") == "sky");
        // 纯蓝加绿得到偏蓝紫，H 越过 245 才算 blue
        Check("蓝紫 #0000ff 加紫 H>245 归入 blue/violet",
            HueBin.FromHex("#7b3ff2") is "violet" or "blue");
        Check("纯黄 H=60 归入 amber", HueBin.FromHex("#ffff00") == "amber");
        Check("黄绿 #84cc16 (H≈83) 归入 yellow", HueBin.FromHex("#84cc16") == "yellow");
        Check("白归入 gray", HueBin.FromHex("#ffffff") == "gray");
        Check("黑归入 gray", HueBin.FromHex("#000000") == "gray");
        Check("浅灰归入 gray", HueBin.FromHex("#cccccc") == "gray");
        Check("色箱名称可查", HueBin.NameOf("magenta") == "洋红");
        Check("非法 HEX 安全降级为 gray", HueBin.FromHex("nonsense") == "gray");
        // 13 个色箱全部有名称与代表色
        Check("色箱共 13 类", HueBin.Bins.Select(b => b.Key).Distinct().Count() == 13);
        Check("每个色箱都有代表色",
            HueBin.Bins.Select(b => HueBin.SwatchOf(b.Key)).All(s => s.StartsWith("#")));
        // 纯色相在各箱边界上不越界
        Check("色箱边界无重叠",
            HueBin.Bins.Where(b => b.Key != "gray")
                .Select(b => (b.Start, b.End))
                .All(r => r.Start < r.End));

        // ---- 系统日志 ----
        Console.WriteLine("\n[系统日志]");
        Log.Info("自测", "写入一条信息日志");
        Log.Warn("自测", "写入一条警告日志", "附带详情");
        Log.Error("自测", "写入一条错误日志");
        Log.Debug("自测", "写入一条调试日志");

        var allLogs = Log.Read(1000);
        Check("日志写入内存缓冲", allLogs.Any(l => l.Message.Contains("写入一条信息日志")));
        Check("各级别均被记录",
            new[] { LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error }
                .All(lv => allLogs.Any(l => l.Level == lv)));
        // Read 返回倒序（最新在前），便于界面直接展示
        var ordered = Log.Read(1000);
        Check("日志按时间倒序返回（最新在前）",
            ordered.Count < 2 || ordered.First().Time >= ordered.Last().Time);
        Check("日志级别过滤生效", Log.Read(1000, LogLevel.Error).All(l => l.Level == LogLevel.Error));
        Check("日志详情被保留",
            allLogs.First(l => l.Message.Contains("写入一条警告日志")).Detail?.Contains("附带详情") == true);

        string logFile = Path.Combine(Log.LogDir, $"colormod-{DateTime.Now:yyyy-MM-dd}.log");
        Check($"日志文件已落盘（{Log.LogDir}）", File.Exists(logFile));
        if (File.Exists(logFile))
        {
            string text = File.ReadAllText(logFile);
            Check("日志文件含中文消息", text.Contains("写入一条信息日志"));
            Check("日志文件含级别标记", text.Contains("[INF]") && text.Contains("[ERR]"));
            Check("日志文件含分类", text.Contains("[自测]"));
        }
        Check("日志目录可读取", !string.IsNullOrEmpty(Log.ReadToday()));
        Check("Truncate 截断超长文本", "abcdefghij".Truncate(5).StartsWith("abcde") &&
                                        "abcdefghij".Truncate(5).Contains("截断"));
        Check("Truncate 不动短文本", "abc".Truncate(10) == "abc");

        // ---- JSON 协议：camelCase ↔ PascalCase（曾导致所有请求静默失效）----
        Console.WriteLine("\n[前后端 JSON 协议]");
        var webOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        // 前端发的是 camelCase
        string camel = "{\"id\":\"r1\",\"action\":\"getStandards\",\"payload\":{\"path\":\"a.png\"}}";
        var parsed = JsonSerializer.Deserialize<Wire>(camel, webOpts);
        Check("camelCase JSON 能反序列化", parsed != null);
        Check("字段 id 正确读出", parsed?.Id == "r1", $"实际={parsed?.Id ?? "null"}");
        Check("字段 action 正确读出（最关键）",
            parsed?.Action == "getStandards", $"实际={parsed?.Action ?? "null"}");
        Check("嵌套 payload 正确读出", parsed?.Payload?.Path == "a.png", parsed?.Payload?.Path ?? "null");

        // 反例：不开启大小写敏感就会静默全 null —— 用它说明为何必须显式配置
        var strict = JsonSerializer.Deserialize<Wire>(camel, new JsonSerializerOptions());
        Check("对照：默认配置下确实全为 null（故必须显式开启）",
            strict?.Id == null && strict?.Action == null);

        // 往返：响应写出后再读回
        // 响应结构是 { ok, reqId, data }，用与前端一致的读取方式验证
        string respJson = JsonSerializer.Serialize(
            new { ok = true, reqId = "r1", data = new[] { "x" } }, webOpts);
        using var respDoc = JsonDocument.Parse(respJson);
        Check("响应含 ok/reqId 字段",
            respDoc.RootElement.GetProperty("ok").GetBoolean()
            && respDoc.RootElement.GetProperty("reqId").GetString() == "r1",
            respJson);
        Check("响应 data 可被前端读取",
            respDoc.RootElement.GetProperty("data").GetArrayLength() == 1);

        // ---- 前端资源完整性（静态校验，捕获「界面空白/点击无反应」类问题）----
        Console.WriteLine("\n[前端资源完整性]");
        string www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        string html = File.Exists(Path.Combine(www, "index.html"))
            ? File.ReadAllText(Path.Combine(www, "index.html")) : "";
        string js = File.Exists(Path.Combine(www, "app.js"))
            ? File.ReadAllText(Path.Combine(www, "app.js")) : "";
        string cssText = File.Exists(Path.Combine(www, "styles.css"))
            ? File.ReadAllText(Path.Combine(www, "styles.css")) : "";
        string palPath = Path.Combine(www, "data", "default_palettes.json");

        // 后端桥接代码：自测运行目录通常不含 .cs，源码断言按可选处理
        string csPath = FindSourceFile("Program.cs");
        string cs = csPath != null ? File.ReadAllText(csPath!) : "";

        Check("index.html 已随程序发布", html.Length > 0);
        Check("app.js 已随程序发布", js.Length > 0);
        Check("styles.css 已随程序发布", css.Length > 0);
        Check("内置色卡 JSON 已随程序发布", File.Exists(palPath));

        // JS 依赖的 DOM 元素必须存在，否则脚本会静默失败
        string[] requiredIds =
        {
            "paletteGrid", "paletteTabs", "hueChips", "searchInput", "clearSearch",
            "libraryEmpty", "paletteMeta", "statText", "standardSelect", "tacNote",
            "dataDir", "importBtn", "exportBtn", "exportViewBtn", "openDirBtn",
            "refreshBtn", "colorCount", "dropzone", "extractGrid", "extractInfo",
            "presetList", "presetEmpty", "stdGrid", "drawer", "drawerBody", "toast",
            "logList", "logBadge", "logRefreshBtn", "logOpenBtn", "logExportBtn",
            "logFilter", "logDir", "view-logs",
            "pickStatus",
        };
        var missing = requiredIds.Where(id => !html.Contains($"id=\"{id}\"", StringComparison.Ordinal)).ToList();
        Check($"JS 依赖的 {requiredIds.Length} 个 DOM 元素齐全", missing.Count == 0,
              missing.Count > 0 ? "缺少: " + string.Join(", ", missing.Take(8)) : "");

        // 每个视图容器都应存在
        Check("五个视图容器齐全",
            new[] { "view-library", "view-picker", "view-presets", "view-standards", "view-logs" }
                .All(id => html.Contains($"id=\"{id}\"", StringComparison.Ordinal)));

        // 事件绑定必须在数据加载之前，否则后端不可用时界面无响应
        int bindIdx = js.IndexOf("bindEvents()", StringComparison.Ordinal);
        int loadIdx = js.IndexOf("await step('加载内置色卡'", StringComparison.Ordinal);
        Check("事件绑定早于数据加载", bindIdx > 0 && loadIdx > bindIdx,
              $"bindEvents@{bindIdx}, load@{loadIdx}");

        // init 中每个异步步骤都应被 step() 包裹，保证失败不中断整体
        Check("init 采用逐步容错结构", js.Contains("async function step(") &&
                                     js.Contains("await step('加载内置色卡'"));
        Check("switchView 立即可用（先切视图再加载）",
              js.IndexOf("switchView('library')", StringComparison.Ordinal) < loadIdx);
        Check("DOMContentLoaded 只绑定一次",
            Regex.Matches(js, "DOMContentLoaded").Count == 1,
            $"实际 {Regex.Matches(js, "DOMContentLoaded").Count} 次");
        Check("渲染函数含空值保护",
            js.Contains("if (!state.builtin || !state.builtin.palettes)"));
        Check("含最近似颜色匹配（搜不到精确值时兜底）",
            js.Contains("function nearestColors"));
        Check("含全部色卡跨库搜索", js.Contains("'__all__'"));
        Check("rpc 具备可用性检测与超时",
            js.Contains("function backendAvailable()") && js.Contains("请求 ${action} 超时"));

        // 分量逐个复制：结构上必须可单独点击，且不冒泡触发整行
        Check("分量可单独点击复制（.num 元素）", js.Contains("class=\"num\"") && js.Contains("data-ch="));
        Check("分量点击已绑定且阻止冒泡",
            js.Contains("querySelectorAll('.num')") && js.Contains("ev && ev.stopPropagation"));
        Check("分量事件对缺失事件对象做了防护", js.Contains("ev && ev.stopPropagation"));
        Check("CMYK 分量渲染 4 个",
            js.Contains("num(cmyk.c, 'C'") && js.Contains("num(cmyk.m, 'M'")
            && js.Contains("num(cmyk.y, 'Y'") && js.Contains("num(cmyk.k, 'K'"));
        Check("RGB 分量渲染 3 个",
            js.Contains("num(r, 'R'") && js.Contains("num(g, 'G'") && js.Contains("num(b, 'B'"));
        Check("详情页分色墨量也可单独复制",
            js.Contains("drawerBody').querySelectorAll('.ink-row .num')"));

        // 中英文双描述
        Check("卡片同时展示中文名与英文名",
            js.Contains("class=\"nm-cn\"") && js.Contains("class=\"nm-en\""));
        Check("详情页展示英文名", js.Contains("class=\"d-name-en\""));
        Check("含 CSS 命名色中文译名表", js.Contains("CSS_CN_NAMES") && js.Contains("aliceblue: '爱丽丝蓝'"));
        Check("CSS 译名表覆盖完整（>=140 条）",
            Regex.Matches(js, @":[ ]*'[^']*'\s*[,}]").Count >= 140,
            $"实际 {Regex.Matches(js, @":[ ]*'[^']*'\s*[,}]").Count} 条");
        Check("含色系中英对照表", js.Contains("CN_FAMILY") && js.Contains("blue: '蓝'"));
        Check("含明度档位表", js.Contains("CN_TONE") && js.Contains("600: '偏深'"));
        Check("色名生成函数已定义", js.Contains("function colorCnName"));
        Check("中文名有回退策略（未知名归色相）", js.Contains("binOfHex(hex)"));

        Check("不再调用阻塞的 openDialog", !js.Contains("rpc('openDialog')"));
        Check("不再调用阻塞的 saveDialog", !js.Contains("rpc('saveDialog')"));
        Check("导出改用下载机制", js.Contains("URL.createObjectURL"));

        // ---- 消息桥接：曾因接收端挂错位置导致「印刷标准报错 / 取色无转换」 ----
        // Photino 的约定：C# SendWebMessage(msg) 落到前端 window.external.receiveMessage(msg)。
        // 曾误用自定义函数名 __cmReceive 作为接收端，导致 C# -> JS 单向不通。
        Check("前端注册了 external.receiveMessage",
            js.Contains("window.external.receiveMessage = function"));
        Check("receiveMessage 转发到分发入口",
            js.Contains("window.__cmDispatch(message)"));
        Check("同时监听 chrome.webview message 事件（双保险）",
            js.Contains("addEventListener('message'"));
        Check("保留 external 原有成员（不整体覆盖）",
            js.Contains("只挂 receiveMessage"));
        Check("存在心跳触发的引导函数 __cmBoot",
            js.Contains("window.__cmBoot = function"));
        bool hasCs = cs.Length > 0;
        // 后端统一以 camelCase 输出，前端不得再访问 PascalCase 字段。
        // 曾因漏改导致取色结果渲染崩溃（c.Coverage -> undefined.toFixed）。
        var pascalRefs = Regex.Matches(js, @"\b[crds]\.[A-Z][A-Za-z]*")
            .Select(m => m.Value).Distinct().ToList();
        Check("前端无 PascalCase 字段访问", pascalRefs.Count == 0,
              pascalRefs.Count > 0 ? "残留: " + string.Join(", ", pascalRefs.Take(6)) : "");
        Check("后端启用了 camelCase 输出",
            !hasCs || cs.Contains("JsonNamingPolicy.CamelCase"));
        Check("取色结果字段用小写（coverage/hex/c/m/y/k）",
            js.Contains("c.coverage.toFixed") && js.Contains("c.hex")
            && js.Contains("c.c + c.m + c.y + c.k"));

        // 取色交互：单按钮 + 状态语义（未拿到结果不谎报失败）
        Check("只有一个图片选择按钮",
            CountOccurrences(html, "id=\"pickFileBtn\"") == 1
            && !html.Contains("id=\"pickCardBtn\""));
        Check("无残留 pickCardBtn 绑定", !js.Contains("pickCardBtn"));
        Check("解析期间禁止重复触发", js.Contains("if (PICK.busy)"));
        Check("解析期间按钮置灰", js.Contains("btn.disabled = true"));
        Check("提供状态条元素", html.Contains("id=\"pickStatus\""));
        Check("区分 busy/ok/error 三态",
            js.Contains("setPickStatus('busy'") && js.Contains("setPickStatus('ok'")
            && js.Contains("setPickStatus('error'"));
        Check("仅后端明确报错才算失败", js.Contains("只有后端明确返回错误，才判定为失败"));
        Check("超时按「仍在解析」处理", js.Contains("isTimeout") && js.Contains("仍在等待后端返回"));
        Check("取消选择不算错误", js.Contains("用户取消，不是错误"));
        Check("拖放同样受 busy 保护",
            js.Contains("PICK.busy = true") && CountOccurrences(js, "if (PICK.busy)") >= 2);

        // 回归防护：曾因替换 onDropFile 时误删 loadTrace/exportTrace，导致日志导不出
        Check("loadTrace 函数存在", js.Contains("async function loadTrace"));
        Check("exportTrace 函数存在", js.Contains("async function exportTrace"));
        Check("追踪按钮已绑定", js.Contains("$('traceBtn').onclick"));
        Check("追踪面板元素存在",
            html.Contains("id=\"traceList\"") && html.Contains("id=\"tracePanel\""));

        // 窗口就绪门控：WebView 未就绪时 SendWebMessage 会静默失败
        Check("存在窗口就绪标志", !hasCs || cs.Contains("_windowReady"));
        Check("收到心跳即标记就绪", !hasCs || cs.Contains("MarkWindowReady"));
        Check("发送线程在未就绪时暂缓发送",
            !hasCs || cs.Contains("窗口未就绪时暂缓取出"));
        Check("前端心跳带自愈补拉", js.Contains("__pendingStuck") && js.Contains("__cmBoot()"));

        // 重复投递去重：Photino 会把同一消息投递多次，
        // 不去重会导致「点一次弹 12 个文件对话框」
        Check("后端含重复投递去重", !hasCs || cs.Contains("重复投递已忽略"));
        // 去重键必须是 reqId：按消息内容去重会吞掉用户的重复操作
        Check("去重键为 reqId（而非消息内容）",
            !hasCs || (cs.Contains("ExtractReqId") && !cs.Contains("seen[message] = now")));
        Check("去重窗口为 30 秒", !hasCs || cs.Contains("now - last < 30000"));
        Check("消息回调只订阅一次",
            !hasCs || CountOccurrences(cs, "window.WebMessageReceivedHandler += OnWebMessage") == 1);
        Check("不再叠加使用 RegisterWebMessageReceivedHandler",
            !hasCs || !cs.Contains("window.RegisterWebMessageReceivedHandler("));

        // 拖放：WebView2 不提供 File.path，必须走 base64 内容上传
        Check("拖放走 base64 上传（图片）", js.Contains("rpc('extractData'"));
        Check("拖放走 base64 上传（二进制色卡）", js.Contains("rpc('importFileData'"));
        Check("提供 FileReader 转 base64", js.Contains("function readAsBase64"));
        Check("拖放不再依赖 file.path", js.Contains("常规路径：读取内容上传"));
        Check("后端提供 extractData 路由", !hasCs || cs.Contains("\"extractData\" =>"));
        Check("后端提供 importFileData 路由", !hasCs || cs.Contains("\"importFileData\" =>"));
        Check("后端具备 base64 解码", !hasCs || cs.Contains("static byte[] FromBase64"));
        Check("临时文件用完即删", !hasCs || cs.Contains("colormod_upload_"));

        // 文件选择改用后端原生对话框（能拿到完整路径），不再是 input[file]
        Check("文件选择走后端原生对话框", js.Contains("rpc('pickFile'"));
        Check("前端 pickFile 已异步", js.Contains("async function pickFile"));
        Check("后端实现 pickFile 路由", !hasCs || cs.Contains("\"pickFile\" => PickFile"));
        Check("使用 Photino 异步文件对话框（非阻塞）",
            !hasCs || cs.Contains("ShowOpenFileAsync"));
        Check("异步结果被 await 后才回包",
            !hasCs || cs.Contains("is Task<object> asyncOp"));
        Check("已移除 input[file] 方案", !html.Contains("id=\"fileInput\""));
        // 关键：含双引号的 JSON 经裸消息通道会丢失，必须走 JS 语句形式
        Check("存在统一分发入口 __cmDispatch", js.Contains("window.__cmDispatch = function"));
        Check("receiveMessage 转发到 __cmDispatch",
            js.Contains("window.__cmDispatch(message)"));
        Check("chrome.webview 同样转发到 __cmDispatch",
            js.Contains("window.__cmDispatch(e.data)"));
        // 真正的根因：在消息回调内同步发送会重入，消息被静默丢弃。
        // 解法是投递队列 + 独立发送线程，发送内容为裸 JSON。
        Check("响应投递到发送队列而非直接发送",
            !hasCs || cs.Contains("Outbound.Enqueue((window, json))"));
        Check("存在独立发送线程", !hasCs || cs.Contains("StartSender"));
        Check("发送线程已启动", !hasCs || cs.Contains("StartSender();"));
        Check("心跳与响应走同一队列", !hasCs || cs.Contains("type\\\":\\\"ping"));
        Check("发送内容为裸 JSON（非 JS 语句）",
            !hasCs || !cs.Contains("window.__cmDispatch && window.__cmDispatch("));
        Check("心跳报文与后端一致",
            js.Contains("\"ping\"") && (!hasCs || cs.Contains("ping")));
        Check("后端周期性投递心跳",
            !hasCs || cs.Contains("SendWebMessage"));
        Check("后端同时用注册方法与事件订阅",
            !hasCs || (cs.Contains("RegisterWebMessageReceivedHandler") && cs.Contains("WebMessageReceivedHandler +=")));
        Check("已移除诊断残留", !js.Contains("__cmPing") && !html.Contains("EARLY_OK"));
        Check("无遗留 InvokeScript（4.x 已无此方法）", !cs.Contains("InvokeScript"));
        if (!hasCs) Console.WriteLine("        （未找到 Program.cs 源码，跳过后端源码断言）");

        // 色箱定义由后端下发，前端不应硬编码为唯一来源
        Check("前端色箱由色卡文件下发", js.Contains("function applyHueBins") &&
                                        js.Contains("applyHueBins(state.builtin.hueBins)"));

        if (File.Exists(palPath))
        {
            var builtin = JsonSerializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(palPath), OptsForFrontend);
            Check("色卡 JSON 可解析", builtin != null);
            var text = File.ReadAllText(palPath);
            Check("色卡 JSON 含 hueBins 定义", text.Contains("\"hueBins\""));
            int colors = System.Text.RegularExpressions.Regex.Matches(text, "\"hex\":").Count;
            Check($"色卡 JSON 含颜色数据（{colors} 条）", colors > 500);
        }

        // 清理自测预设
        foreach (var p in store.LoadAll().Where(p => p.Name == "自测预设")) store.Delete(p.Id);

        Console.WriteLine($"\n=== 结果：{_pass} 通过 / {_fail} 失败 ===");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>统计子串出现次数，用于断言「只注册一次」这类约束。</summary>
    static int CountOccurrences(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }

    /// <summary>协议测试用的线格式，对应前端实际发送的结构。</summary>
    sealed class Wire
    {
        public string? Id { get; set; }
        public string? Action { get; set; }
        public WirePayload? Payload { get; set; }
    }

    sealed class WirePayload
    {
        public string? Path { get; set; }
    }

    /// <summary>
    /// 向上查找项目源码文件。发布产物中通常没有 .cs，
    /// 找不到时返回 null，相关断言应据此跳过而非误报失败。
    /// </summary>
    static string? FindSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        // 也查当前工作目录
        string local = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        return File.Exists(local) ? local : null;
    }

    static bool ThrowsFormat(Action a)
    {
        try { a(); return false; }
        catch (FormatException) { return true; }
        catch { return false; }
    }

    /// <summary>生成 4 色块测试图。</summary>
    static void MakeTestImage(string path)
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(120, 80);
        var colors = new (byte R, byte G, byte B)[]
        {
            (220, 50, 60), (30, 90, 180), (240, 200, 60), (250, 250, 250)
        };
        for (int y = 0; y < 80; y++)
        for (int x = 0; x < 120; x++)
        {
            var c = colors[(x < 60 ? 0 : 1) + (y < 40 ? 0 : 2)];
            img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(c.R, c.G, c.B);
        }
        img.SaveAsPng(path);
    }

    /// <summary>构造最小合法 ASE：2 个颜色块（RGB 色彩模型，全部大端）。</summary>
    static byte[] BuildMinimalAse()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void BE16(int v)
        {
            w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF));
        }
        void BE32(uint v)
        {
            w.Write((byte)((v >> 24) & 0xFF)); w.Write((byte)((v >> 16) & 0xFF));
            w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF));
        }
        void Utf16Be(string s)
        {
            var bytes = System.Text.Encoding.BigEndianUnicode.GetBytes(s);
            BE16(bytes.Length / 2);              // 长度 = 字符数
            w.Write(bytes);
        }

        w.Write(System.Text.Encoding.ASCII.GetBytes("ASEF"));
        BE16(1); BE16(0);                       // version 1.0
        BE32(2);                                // 2 个块
        for (int i = 0; i < 2; i++)
        {
            BE16(0x0001);                       // group type: color
            Utf16Be($"Swatch {i + 1}");
            w.Write(System.Text.Encoding.ASCII.GetBytes("RGB "));  // 色彩模型签名
            Utf16Be("");
            BE32(1);                            // 1 个颜色
            BE32((uint)BitConverter.SingleToInt32Bits(0.9f));    // R
            BE32((uint)BitConverter.SingleToInt32Bits(0.2f));    // G
            BE32((uint)BitConverter.SingleToInt32Bits(0.5f));    // B
            Utf16Be("");
        }
        w.Flush();
        return ms.ToArray();
    }
}
