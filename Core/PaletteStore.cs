using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColorCardStudio.Core;

public sealed class Palette
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    [JsonPropertyName("name")] public string Name { get; set; } = "未命名预设";
    [JsonPropertyName("source")] public string Source { get; set; } = "custom";   // custom / import / builtin
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("standardId")] public string StandardId { get; set; } = "pso-coated-v3";
    [JsonPropertyName("colors")] public List<PaletteColor> Colors { get; set; } = new();
}

public sealed class PaletteColor
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hex")] public string Hex { get; set; } = "#000000";
    [JsonPropertyName("hueBin")] public string HueBin { get; set; } = "gray";
    [JsonPropertyName("family")] public string Family { get; set; } = "";
}

/// <summary>
/// 预设持久化：用户自定义 / 导入的色卡存 JSON。
/// 数据目录：Windows 用 %APPDATA%\ColorCardStudio，macOS/Linux 用 ~/.config/colormod。
/// </summary>
public sealed class PaletteStore
{
    readonly string _dir;
    readonly string _file;
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public PaletteStore()
    {
        _dir = ResolveDataDir();
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "palettes.json");
    }

    public static string ResolveDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "ColorCardStudio");
        }
        string? cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(cfg))
            cfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(cfg, "colormod");
    }

    public string DataDir => _dir;

    public List<Palette> LoadAll()
    {
        if (!File.Exists(_file)) return new();
        try
        {
            var json = File.ReadAllText(_file, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<Palette>>(json, Opts) ?? new();
        }
        catch
        {
            // 配置损坏时保留副本，避免用户数据被静默覆盖
            try { File.Copy(_file, _file + ".broken." + DateTimeOffset.Now.ToUnixTimeSeconds(), true); } catch { }
            return new();
        }
    }

    public void SaveAll(List<Palette> palettes)
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(palettes, Opts), Encoding.UTF8);
        File.Copy(tmp, _file, true);
        File.Delete(tmp);
    }

    public Palette Add(Palette p)
    {
        var all = LoadAll();
        all.Insert(0, p);
        SaveAll(all);
        return p;
    }

    public bool Delete(string id)
    {
        var all = LoadAll();
        int n = all.RemoveAll(p => p.Id == id);
        if (n > 0) SaveAll(all);
        return n > 0;
    }

    /// <summary>导出为 JSON 字符串。</summary>
    public string ExportJson(IEnumerable<Palette> palettes) =>
        JsonSerializer.Serialize(palettes, Opts);

    // ---------- 第三方色卡导入 ----------

    /// <summary>
    /// 导入色卡文件，返回解析出的全部预设与格式标识。
    /// 支持：.json（ColorMod 导出的预设，可含多个，逐个保留原名）、
    /// .gpl、.css、.hex/.txt。ASE 为二进制格式，走 <see cref="ImportAse"/>。
    /// </summary>
    public (List<Palette> palettes, string format) ImportFileAll(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".ase" or ".aseu")
            throw new NotSupportedException("ASE 为二进制格式，请改用 ASE 导入入口");

        string text = SafeReadText(path);
        string baseName = Path.GetFileNameWithoutExtension(path);

        switch (ext)
        {
            case ".json":
                {
                    var list = ImportJsonAll(text, baseName);
                    if (list.Count == 0)
                        throw new FormatException("未能从 JSON 解析出色卡，请确认是 ColorMod 导出的预设文件");
                    return (list, $"ColorMod 预设 JSON（{list.Count} 个预设 / {list.Sum(p => p.Colors.Count)} 色）");
                }
            case ".gpl":
                return (new List<Palette> { ImportGpl(text, baseName) }, "GIMP 色卡 (.gpl)");
            case ".css":
                return (new List<Palette> { ImportCss(text, baseName) }, "CSS 变量");
            default:
                return (new List<Palette> { ImportHexList(text, baseName) }, "HEX 列表");
        }
    }

    /// <summary>导入单个预设（多预设 JSON 会返回第一个）。</summary>
    public (Palette palette, int count, string format) ImportFile(string path)
    {
        var (palettes, format) = ImportFileAll(path);
        return (palettes[0], 0, format);
    }

    /// <summary>
    /// 导入 ColorMod 导出的 JSON 预设，实现「导出 → 导入」回环。
    /// 兼容单个预设对象与预设数组两种结构，逐色校验并补全色相分类。
    /// 注意：数组结构会逐个导入为独立预设，以保留各自名称；
    /// 而前端一次只保存一个 Palette，故用 ImportJsonAll 返回全部。
    /// </summary>
    public List<Palette> ImportJsonPalettes(string text, string fallbackName)
    {
        var all = ImportJsonAll(text, fallbackName);
        if (all.Count == 0)
            throw new FormatException("未能从 JSON 解析出色卡，请确认是 ColorMod 导出的预设文件");
        return all;
    }

    /// <summary>解析导出的 JSON，返回全部预设（保持原名，便于逐个导入）。</summary>
    List<Palette> ImportJsonAll(string text, string fallbackName)
    {
        List<Palette>? list = null;
        try { list = JsonSerializer.Deserialize<List<Palette>>(text, Opts); }
        catch { /* 退化为单对象解析 */ }

        if (list is null || list.Count == 0)
        {
            try
            {
                var one = JsonSerializer.Deserialize<Palette>(text, Opts);
                if (one?.Colors.Count > 0) list = new List<Palette> { one };
            }
            catch { /* 保持 null */ }
        }

        if (list is null || list.Count == 0) return new List<Palette>();

        var result = new List<Palette>();
        int seq = 0;
        foreach (var src in list)
        {
            seq++;
            if (src?.Colors is null) continue;

            var colors = new List<PaletteColor>();
            foreach (var c in src.Colors)
            {
                string hx = (c.Hex ?? "").Trim().ToLowerInvariant();
                if (!hx.StartsWith('#')) hx = "#" + hx;
                try { _ = ColorMath.FromHex(hx); }          // 校验有效性，无效色跳过
                catch { continue; }
                colors.Add(new PaletteColor
                {
                    Name = c.Name ?? "",
                    Hex = hx,
                    HueBin = string.IsNullOrEmpty(c.HueBin) ? ClassifyHex(hx) : c.HueBin!,
                    Family = c.Family ?? "",
                });
            }
            if (colors.Count == 0) continue;

            string name = !string.IsNullOrWhiteSpace(src.Name) ? src.Name!.Trim()
                         : (list.Count == 1 ? fallbackName : $"{fallbackName} #{seq}");
            result.Add(new Palette
            {
                Name = name,
                Source = "import",
                StandardId = src.StandardId ?? "pso-coated-v3",
                Colors = colors,
            });
        }
        return result;
    }

    static string SafeReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // 跳过 UTF-8 BOM
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>按色相归类，供导入时补全 hueBin。</summary>
    static string ClassifyHex(string hex) => HueBin.FromHex(hex);

    /// <summary>
    /// Adobe Swatch Exchange 二进制格式导入。
    /// 块结构：type(2) + name + colorModel(4) + name + colorCount(4) + colors + name(尾)
    /// 颜色分量个数依色彩模型：RGB/Gray=3，CMYK=4，Lab=3，HSB=3。
    /// </summary>
    public Palette ImportAse(byte[] data, string name)
    {
        var colors = new List<PaletteColor>();
        // 全程只用 MemoryStream 定位，避免 BinaryReader 预读缓冲导致位置错乱
        using var ms = new MemoryStream(data);

        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "ASEF")
            throw new FormatException("不是有效的 ASE 色卡文件");

        // 注意：ASE 所有多字节整数均为 Big-Endian，必须手动按大端组装
        ushort ReadBE16()
        {
            if (ms.Position + 2 > ms.Length) throw new EndOfStreamException();
            int p = (int)ms.Position;
            ms.Position += 2;
            return (ushort)((data[p] << 8) | data[p + 1]);
        }
        uint ReadBE32()
        {
            if (ms.Position + 4 > ms.Length) throw new EndOfStreamException();
            int p = (int)ms.Position;
            ms.Position += 4;
            return ((uint)data[p] << 24) | ((uint)data[p + 1] << 16) | ((uint)data[p + 2] << 8) | data[p + 3];
        }
        void SkipString()
        {
            if (ms.Position + 2 > ms.Length) { ms.Position = ms.Length; return; }
            int len = ReadBE16();
            long skip = (long)len * 2;
            ms.Position = ms.Position + skip > ms.Length ? ms.Length : ms.Position + skip;
        }

        // 跳过 4 字节文件签名 "ASEF"（已在上面校验过）
        ms.Position = 4;

        ReadBE16();                             // version major
        ReadBE16();                             // version minor
        uint blockCount = ReadBE32();

        // 防御性上限：异常文件不应导致长时间循环
        if (blockCount > 100_000) throw new FormatException("ASE 文件块数异常");

        for (uint i = 0; i < blockCount; i++)
        {
            try
            {
                if (ms.Position + 2 > ms.Length) break;
                ushort type = ReadBE16();
                SkipString();
                if (type != 0x0001) continue;   // 跳过非颜色块（组/名称）

                if (ms.Position + 4 > ms.Length) break;
                uint model = ReadBE32();
                SkipString();
                if (ms.Position + 4 > ms.Length) break;
                uint count = ReadBE32();
                if (count > 100_000) break;

                // 色彩模型为 4 字节大端签名
                int comps = model switch
                {
                    0x434D594B => 4,   // 'CMYK'
                    0x4C414220 => 3,   // 'LAB '
                    0x434D5920 => 3,   // 'CMY '
                    _ => 3,            // 'RGB ' / 'HSB ' / 'GRAY'
                };
                bool isCmykModel = model == 0x434D594B;

                for (uint j = 0; j < count; j++)
                {
                    long need = (long)comps * 4;
                    if (ms.Position + need > ms.Length)
                        return new Palette { Name = name, Source = "import", Colors = colors };

                    var v = new float[comps];
                    for (int k = 0; k < comps; k++) v[k] = BitConverter.Int32BitsToSingle((int)ReadBE32());

                    (byte r, byte g, byte b) = isCmykModel
                        ? CmykToRgb(v[0], v[1], v[2], v[3])
                        : (F(v[0]), F(v[1]), F(v[2]));

                    var rgb = new ColorMath.Rgb(r, g, b);
                    string hx = ColorMath.ToHex(rgb);
                    colors.Add(new PaletteColor
                    {
                        Name = $"色 {colors.Count + 1}", Hex = hx, HueBin = ClassifyHex(hx),
                    });
                }
                SkipString();
            }
            catch (EndOfStreamException)
            {
                break;   // 文件被截断，保留已解析出的颜色
            }
        }
        return new Palette { Name = name, Source = "import", Colors = colors };

        static byte F(float x) => (byte)Math.Clamp(Math.Round(x <= 1f ? x * 255f : x), 0, 255);
    }

    /// <summary>ASE 内置 CMYK -> RGB（简化墨点模型，用于预览显示）。</summary>
    static (byte R, byte G, byte B) CmykToRgb(float c, float m, float y, float k)
    {
        static float N(float v) => v is > 1f or < 0f ? v / 255f : v;
        c = Math.Clamp(N(c), 0, 1); m = Math.Clamp(N(m), 0, 1);
        y = Math.Clamp(N(y), 0, 1); k = Math.Clamp(N(k), 0, 1);
        return ((byte)Math.Round(255 * (1 - c) * (1 - k)),
                (byte)Math.Round(255 * (1 - m) * (1 - k)),
                (byte)Math.Round(255 * (1 - y) * (1 - k)));
    }


    /// <summary>GIMP 色卡 (.gpl)：一行 "R G B\t名称"。</summary>
    public Palette ImportGplText(string text, string name) => ImportGpl(text, name);

    Palette ImportGpl(string text, string name)
    {
        var colors = new List<PaletteColor>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("GIMP", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = line.Split('\t');
            var nums = parts[0].Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length < 3) continue;
            if (!int.TryParse(nums[0], out int r) || !int.TryParse(nums[1], out int g) || !int.TryParse(nums[2], out int b))
                continue;
            var rgb = new ColorMath.Rgb((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
            string hx = ColorMath.ToHex(rgb);
            colors.Add(new PaletteColor
            {
                Name = parts.Length > 1 ? parts[1].Trim() : $"色 {colors.Count + 1}",
                Hex = hx,
                HueBin = ClassifyHex(hx),
            });
        }
        return new Palette { Name = name, Source = "import", Colors = colors };
    }

    /// <summary>CSS 变量：--name: #hex;</summary>
    public Palette ImportCssText(string text, string name) => ImportCss(text, name);

    Palette ImportCss(string text, string name)
    {
        var colors = new List<PaletteColor>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, @"--([A-Za-z0-9_-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;"))
        {
            string hx = m.Groups[2].Value.ToLowerInvariant();
            colors.Add(new PaletteColor { Name = m.Groups[1].Value, Hex = hx, HueBin = ClassifyHex(hx) });
        }
        return new Palette { Name = name, Source = "import", Colors = colors };
    }

    /// <summary>通用列表：一行一个 #RRGGBB 或 "名称 #RRGGBB"。</summary>
    public Palette ImportHexListText(string text, string name) => ImportHexList(text, name);

    Palette ImportHexList(string text, string name)
    {
        var colors = new List<PaletteColor>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') && !line.StartsWith("#0") && line.Length < 5) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"([A-Za-z0-9 _\-\u4e00-\u9fa5]+)?\s*#([0-9a-fA-F]{6})");
            if (!m.Success) continue;
            string hx = "#" + m.Groups[2].Value.ToLowerInvariant();
            colors.Add(new PaletteColor
            {
                Name = string.IsNullOrWhiteSpace(m.Groups[1].Value) ? $"色 {colors.Count + 1}" : m.Groups[1].Value.Trim(),
                Hex = hx,
                HueBin = ClassifyHex(hx),
            });
        }
        return new Palette { Name = name, Source = "import", Colors = colors };
    }
}
