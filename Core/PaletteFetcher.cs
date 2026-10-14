using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ColorCardStudio.Core;

/// <summary>
/// 内置色卡抓取器：联网获取主流色卡并生成 wwwroot/data/default_palettes.json。
/// 以 --fetch-palettes 命令行参数运行，无需外部脚本工具。
/// 网络策略：unpkg / jsdelivr 直连优先，GitHub 走镜像加速，
/// 单个源失败不影响其他源（失败会记录并跳过）。
/// </summary>
public static class PaletteFetcher
{
    /// <summary>GitHub 加速镜像前缀，按顺序尝试。</summary>
    static readonly string[] GithubMirrors =
    {
        "https://ghfast.top/",
        "https://ghproxy.net/",
        "https://gh-proxy.com/",
    };

    /// <summary>GitHub raw 原始地址（相对路径部分）。</summary>
    const string GithubRawBase = "https://raw.githubusercontent.com/";

    /// <summary>构造 GitHub raw 地址的候选列表：直连 + 各镜像。</summary>
    static string[] GithubCandidates(string path)
    {
        var list = new List<string> { GithubRawBase + path };
        foreach (var m in GithubMirrors) list.Add(m + GithubRawBase + path);
        return list.ToArray();
    }

    sealed record Fetched(string Name, string Source, List<RawColor> Colors);

    sealed record RawColor(string Name, string Hex, string Family);

    // ---------- 数据源定义 ----------

    static readonly string[] RadixFamilies =
    {
        "gray", "mauve", "slate", "sage", "olive", "sand",
        "tomato", "red", "ruby", "pink", "plum", "purple", "violet",
        "iris", "indigo", "blue", "cyan", "teal", "jade", "green", "lime", "amber", "yellow", "orange",
    };

    public sealed record Report(int Palettes, int Colors, List<string> Ok, List<string> Failed);

    /// <summary>抓取全部色卡并写入目标 JSON。overwrite=false 时若已存在则跳过。</summary>
    public static async Task<Report> RunAsync(string outputPath, bool overwrite, CancellationToken ct = default)
    {
        if (File.Exists(outputPath) && !overwrite)
        {
            var existing = TryReadCount(outputPath);
            Console.WriteLine($"已存在 {outputPath}（{existing} 色）。使用 --overwrite 强制重新抓取。");
            return new Report(0, existing, new List<string> { "已跳过：目标文件存在" }, new List<string>());
        }

        var ok = new List<string>();
        var failed = new List<string>();
        var results = new List<Fetched>();

        // ---- 1) Tailwind CSS v4（OKLCH，需转换）----
        try
        {
            var urls = new[]
            {
                "https://unpkg.com/tailwindcss@4.0.0/theme.css",
                "https://cdn.jsdelivr.net/npm/tailwindcss@4.0.0/theme.css",
            }.Concat(GithubCandidates("tailwindlabs/tailwindcss/main/packages/tailwindcss/theme.css"))
             .ToArray();
            var txt = await GetWithFallbackAsync(urls, ct);
            var colors = ParseTailwind(txt);
            if (colors.Count > 0)
            {
                results.Add(new Fetched("Tailwind CSS v4",
                    "https://tailwindcss.com/docs/theme", colors));
                ok.Add($"Tailwind CSS v4: {colors.Count}");
            }
            else failed.Add("Tailwind CSS v4: 未解析到颜色");
        }
        catch (Exception ex) { failed.Add($"Tailwind CSS v4: {ex.Message}"); }

        // ---- 2) Material Design 3（参考调色板 scss）----
        try
        {
            var urls = GithubCandidates("material-components/material-web/main/tokens/versions/v0_192/_md-ref-palette.scss")
                .Concat(GithubCandidates("material-components/material-web/main/tokens/_md-sys-color.scss"))
                .ToArray();
            var txt = await GetWithFallbackAsync(urls, ct);
            var colors = ParseMaterial(txt);
            if (colors.Count > 0)
            {
                results.Add(new Fetched("Material Design 3",
                    "https://m3.material.io/styles/color/system/colors", colors));
                ok.Add($"Material Design 3: {colors.Count}");
            }
            else failed.Add("Material Design 3: 未解析到颜色");
        }
        catch (Exception ex) { failed.Add($"Material Design 3: {ex.Message}"); }

        // ---- 3) Bootstrap 5（scss 变量）----
        try
        {
            var urls = new[]
            {
                "https://unpkg.com/bootstrap@5.3.8/scss/_variables.scss",
                "https://cdn.jsdelivr.net/npm/bootstrap@5.3.8/scss/_variables.scss",
            }.Concat(GithubCandidates("twbs/bootstrap/main/scss/_variables.scss")).ToArray();
            var txt = await GetWithFallbackAsync(urls, ct);
            var colors = ParseBootstrap(txt);
            if (colors.Count > 0)
            {
                results.Add(new Fetched("Bootstrap 5", "https://getbootstrap.com/docs/5.3/customize/", colors));
                ok.Add($"Bootstrap 5: {colors.Count}");
            }
            else failed.Add("Bootstrap 5: 未解析到颜色");
        }
        catch (Exception ex) { failed.Add($"Bootstrap 5: {ex.Message}"); }

        // ---- 4) Radix Colors（逐色系抓 CSS）----
        try
        {
            var colors = await FetchRadixAsync(ct);
            if (colors.Count > 0)
            {
                results.Add(new Fetched("Radix Colors", "https://www.radix-ui.com/colors", colors));
                ok.Add($"Radix Colors: {colors.Count}");
            }
            else failed.Add("Radix Colors: 未解析到颜色");
        }
        catch (Exception ex) { failed.Add($"Radix Colors: {ex.Message}"); }

        // ---- 5) Open Color（JSON）----
        try
        {
            var txt = await GetWithFallbackAsync(new[] { "https://unpkg.com/open-color@1.9.1/open-color.json" }, ct);
            var colors = ParseOpenColor(txt);
            if (colors.Count > 0)
            {
                results.Add(new Fetched("Open Color", "https://yeun.github.io/open-color/", colors));
                ok.Add($"Open Color: {colors.Count}");
            }
            else failed.Add("Open Color: 未解析到颜色");
        }
        catch (Exception ex) { failed.Add($"Open Color: {ex.Message}"); }

        // ---- 6) CSS 命名颜色 ----
        try
        {
            var txt = await GetWithFallbackAsync(new[] { "https://unpkg.com/color-name@1.1.4/index.js" }, ct);
            var colors = ParseColorName(txt);
            if (colors.Count > 0)
            {
                results.Add(new Fetched("CSS 命名颜色", "https://www.w3.org/TR/css-color-4/", colors));
                ok.Add($"CSS 命名颜色: {colors.Count}");
            }
            else failed.Add("CSS 命名颜色: 未解析到颜色");
        }
        catch (Exception ex) { failed.Add($"CSS 命名颜色: {ex.Message}"); }

        if (results.Count == 0)
        {
            Console.WriteLine("所有色卡源均获取失败，请检查网络后重试。");
            return new Report(0, 0, ok, failed);
        }

        // ---- 写盘：按色相归类并去重 ----
        var sb = new StringBuilder();
        sb.Append("{\"version\":1,\"palettes\":{");
        bool firstPalette = true;
        int total = 0;

        foreach (var f in results)
        {
            var seen = new HashSet<string>();
            var items = new List<(string Name, string Hex, string Family)>();
            foreach (var c in f.Colors)
            {
                string hx = c.Hex.ToLowerInvariant();
                if (hx.Length != 7 || !Regex.IsMatch(hx, "^#[0-9a-f]{6}$")) continue;
                if (!seen.Add(hx)) continue;
                items.Add((c.Name, hx, c.Family));
            }

            if (items.Count == 0) continue;

            if (!firstPalette) sb.Append(',');
            firstPalette = false;
            total += items.Count;

            sb.Append(JsonSerializer.Serialize(f.Name)).Append(":{\"source\":")
              .Append(JsonSerializer.Serialize(f.Source))
              .Append(",\"count\":").Append(items.Count).Append(",\"colors\":[");

            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var (name, hex, family) = items[i];
                sb.Append("{\"name\":").Append(JsonSerializer.Serialize(name))
                  .Append(",\"hex\":").Append(JsonSerializer.Serialize(hex))
                  .Append(",\"family\":").Append(JsonSerializer.Serialize(family))
                  .Append(",\"hueBin\":").Append(JsonSerializer.Serialize(HueBin.FromHex(hex)))
                  .Append('}');
            }
            sb.Append("]}");
            Console.WriteLine($"  [ OK ] {f.Name}: {items.Count} 色");
        }

        // hueBins 定义随文件下发，前端不再硬编码
        sb.Append("},\"hueBins\":[");
        var seenKeys = new HashSet<string>();
        bool firstBin = true;
        foreach (var (key, name, _, _) in HueBin.Bins)
        {
            if (!seenKeys.Add(key)) continue;
            if (!firstBin) sb.Append(',');
            firstBin = false;
            sb.Append("{\"key\":").Append(JsonSerializer.Serialize(key))
              .Append(",\"name\":").Append(JsonSerializer.Serialize(name))
              .Append(",\"swatch\":").Append(JsonSerializer.Serialize(HueBin.SwatchOf(key)))
              .Append('}');
        }
        sb.Append("]}");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));

        foreach (var f in failed) Console.WriteLine($"  [FAIL] {f}");
        Console.WriteLine($"共 {results.Count} 套色卡 / {total} 色 -> {outputPath}");
        return new Report(results.Count, total, ok, failed);
    }


    static int TryReadCount(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("palettes", out var ps))
            {
                int sum = 0;
                foreach (var p in ps.EnumerateObject())
                    sum += p.Value.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                return sum;
            }
        }
        catch { }
        return 0;
    }

    // ---------- HTTP ----------

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ColorMod/1.0");
        return h;
    }

    /// <summary>按顺序尝试多个 URL，全部失败则抛出最后一个异常。</summary>
    static async Task<string> GetWithFallbackAsync(string[] urls, CancellationToken ct)
    {
        Exception? last = null;

        foreach (var url in urls)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var resp = await Http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        last = new HttpRequestException($"HTTP {(int)resp.StatusCode} @ {url}");
                    }
                    else
                    {
                        var text = await resp.Content.ReadAsStringAsync(ct);
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                        last = new InvalidDataException($"空响应 @ {url}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }

                if (attempt == 0) await Task.Delay(700, ct);   // 退避后重试一次
            }
        }
        throw last ?? new HttpRequestException("所有地址均获取失败");
    }

    // ---------- 解析器 ----------

    /// <summary>
    /// Tailwind v4 使用 OKLCH，需转换到 sRGB。
    /// 注意 L 既可能是百分数（97.1%）也可能是小数（0.971），两者都要支持。
    /// </summary>
    static List<RawColor> ParseTailwind(string css)
    {
        var list = new List<RawColor>();
        foreach (Match m in Regex.Matches(css,
            @"--color-([a-z]+)-(\d+):\s*oklch\(\s*([\d.]+)(%?)\s+([\d.]+)\s+([\d.]+)\s*\)"))
        {
            double lRaw = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            double L = m.Groups[4].Value == "%" ? lRaw / 100.0 : lRaw;
            var (r, g, b) = OklchToRgb(L,
                double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture));
            list.Add(new RawColor($"{m.Groups[1].Value}-{m.Groups[2].Value}", ToHex(r, g, b), m.Groups[1].Value));
        }
        return list;
    }

    /// <summary>Material 参考调色板：'primary40': if($exclude-hardcoded-values, null, #65558f),</summary>
    static List<RawColor> ParseMaterial(string scss)
    {
        var list = new List<RawColor>();
        foreach (Match m in Regex.Matches(scss,
            @"'([a-z0-9-]+)'\s*:\s*if\([^,]+,\s*[^,]+,\s*(#[0-9a-fA-F]{6})\s*\)"))
        {
            string nm = m.Groups[1].Value;
            string fam = Regex.Replace(nm, @"\d+$", "");
            if (fam.Length == 0) fam = nm;
            string step = Regex.Match(nm, @"(\d+)$").Value;
            list.Add(new RawColor(string.IsNullOrEmpty(step) ? nm : $"{fam}-{step}",
                                  m.Groups[2].Value.ToLowerInvariant(), fam));
        }
        return list;
    }

    /// <summary>Bootstrap scss 变量：$blue: #0d6efd !default;</summary>
    static List<RawColor> ParseBootstrap(string scss)
    {
        var list = new List<RawColor>();
        var seen = new HashSet<string>();
        foreach (Match m in Regex.Matches(scss, @"\$([a-z0-9-]+):\s*(#[0-9a-fA-F]{6})\s*!default"))
        {
            string nm = m.Groups[1].Value;
            if (!seen.Add(nm)) continue;   // 变量引用会重复定义，取首个字面量
            list.Add(new RawColor(nm, m.Groups[2].Value.ToLowerInvariant(), ""));
        }
        return list;
    }

    static async Task<List<RawColor>> FetchRadixAsync(CancellationToken ct)
    {
        var all = new List<RawColor>();
        foreach (var fam in RadixFamilies)
        {
            try
            {
                var css = await GetWithFallbackAsync(new[] { $"https://unpkg.com/@radix-ui/colors@latest/{fam}.css" }, ct);
                int cut = css.IndexOf("@supports", StringComparison.Ordinal);
                string head = cut > 0 ? css[..cut] : css;   // 只要 light 基础段
                foreach (Match m in Regex.Matches(head, @"--[a-z]+-(\d+):\s*(#[0-9a-fA-F]{6})\s*;"))
                    all.Add(new RawColor($"{fam}-{m.Groups[1].Value}", m.Groups[2].Value.ToLowerInvariant(), fam));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Radix/{fam} 跳过: {ex.Message}");
            }
        }
        return all;
    }

    /// <summary>Open Color JSON：{ "white": "#ffffff", "gray": ["#f8f9fa", ...], "blue": [0,12,34] }</summary>
    static List<RawColor> ParseOpenColor(string json)
    {
        var list = new List<RawColor>();
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                list.Add(new RawColor(prop.Name, prop.Value.GetString()!.ToLowerInvariant(), prop.Name));
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    string? hx = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Array when item.GetArrayLength() == 3 => ToHex(
                            item[0].GetInt32(), item[1].GetInt32(), item[2].GetInt32()),
                        _ => null,
                    };
                    i++;
                    if (!string.IsNullOrEmpty(hx) && Regex.IsMatch(hx, "^#[0-9a-fA-F]{6}$"))
                        list.Add(new RawColor($"{prop.Name}-{i}", hx!.ToLowerInvariant(), prop.Name));
                }
            }
        }
        return list;
    }

    /// <summary>color-name 模块：{ "aliceblue": [240,248,255], ... }</summary>
    static List<RawColor> ParseColorName(string js)
    {
        var list = new List<RawColor>();
        foreach (Match m in Regex.Matches(js, @"""([a-z]+)""\s*:\s*\[\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\]"))
            list.Add(new RawColor(m.Groups[1].Value,
                ToHex(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)),
                ""));
        return list;
    }

    // ---------- 色彩转换 ----------

    static string ToHex(int r, int g, int b) => $"#{r:x2}{g:x2}{b:x2}";

    /// <summary>OKLCH -> sRGB（Björn Ottosson 的标准矩阵法）。</summary>
    static (int R, int G, int B) OklchToRgb(double L, double C, double H)
    {
        double h = H * Math.PI / 180.0;
        double a = C * Math.Cos(h), b = C * Math.Sin(h);

        double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = L - 0.0894841775 * a - 1.2914855480 * b;

        double lc = l_ * l_ * l_, mc = m_ * m_ * m_, sc = s_ * s_ * s_;

        double r = +4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
        double g = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
        double bb = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;

        static double Gam(double x)
        {
            x = Math.Clamp(x, 0, 1);
            return x <= 0.0031308 ? x * 12.92 : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
        }

        return ((int)Math.Round(Gam(r) * 255),
                (int)Math.Round(Gam(g) * 255),
                (int)Math.Round(Gam(bb) * 255));
    }
}
