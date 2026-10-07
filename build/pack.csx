#!/usr/bin/env dotnet-script
// =============================================================================
// ColorMod 跨平台打包脚本
//
// 用法：
//   dotnet-script build/pack.csx                     # 打包当前平台
//   dotnet-script build/pack.csx -- --all            # 打包全部平台
//   dotnet-script build/pack.csx -- --runtime linux-x64
//   dotnet-script build/pack.csx -- --self-contained # 每个平台独立运行，无需装 .NET
//   dotnet-script build/pack.csx -- --zip            # 打包后生成 zip
//   dotnet-script build/pack.csx -- --clean          # 先清理再打包
//
// 产物统一输出到 artifacts/<rid>/
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.IO.Compression;

// ---------------------------------------------------------------- 参数解析
// 从命令行读取 --xxx 参数（不依赖宿主注入的 args，兼容性更好）
string[] rawArgs = ReadScriptArgs();

static string[] ReadScriptArgs()
{
    var all = Environment.GetCommandLineArgs();
    var sep = Array.IndexOf(all, "--");   // dotnet-script 用 -- 分隔脚本参数
    if (sep >= 0 && sep + 1 < all.Length)
        return all.Skip(sep + 1).ToArray();

    // 回退：直接收集所有 --xxx 形式的参数
    return all.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray();
}

bool flag(string name) => rawArgs.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)
                                       || a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
string opt(string name)
{
    for (int i = 0; i < rawArgs.Length - 1; i++)
        if (rawArgs[i].Equals("--" + name, StringComparison.OrdinalIgnoreCase))
            return rawArgs[i + 1];
    return "";
}

// 收集全部指定值：支持 --runtime a --runtime b 与 --runtime a,b
List<string> optAll(string name)
{
    var list = new List<string>();
    for (int i = 0; i < rawArgs.Length - 1; i++)
    {
        if (!rawArgs[i].Equals("--" + name, StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var part in rawArgs[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            list.Add(part.Trim());
    }
    return list;
}

bool all = flag("all");
bool selfContained = flag("self-contained");
bool makeZip = flag("zip");
bool makeSetup = flag("setup") || makeZip;      // setup 默认为 zip 时附带
bool makeDmg = flag("dmg");
bool makeLinuxPkg = flag("linux-pkg") || flag("deb");
bool clean = flag("clean");
string configuration = opt("configuration");
if (string.IsNullOrEmpty(configuration)) configuration = "Release";

// ---------------------------------------------------------------- 路径
var root = FindRepoRoot();
string projectFile = Path.Combine(root, "ColorCardStudio.csproj");
string artifacts = Path.Combine(root, "artifacts");

// 控制台按 UTF-8 输出，避免中文在部分终端显示为乱码
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

void Say(string msg)
{
    var t = DateTime.Now.ToString("HH:mm:ss");
    Console.WriteLine($"[{t}] {msg}");
}

// ---------------------------------------------------------------- 目标矩阵
var current = RuntimeInformation.RuntimeIdentifier;

var targets = new List<(string Rid, string Name, bool NeedsWebView)>
{
    ("win-x64",   "Windows x64",   true),
    ("win-arm64", "Windows ARM64", true),
    ("linux-x64", "Linux x64",     true),
    ("linux-arm64","Linux ARM64",  true),
    ("osx-x64",   "macOS Intel",   true),
    ("osx-arm64", "macOS Apple Silicon", true),
};

var wanted = optAll("runtime");
IEnumerable<(string Rid, string Name, bool NeedsWebView)> selected =
    wanted.Count > 0
        ? targets.Where(t => wanted.Any(w => t.Rid.Equals(w, StringComparison.OrdinalIgnoreCase)))
        : all ? targets
        : new[] { (current, CurrentName(current), true) };

static string CurrentName(string rid) => rid switch
{
    var r when r.StartsWith("win") => $"Windows ({r})",
    var r when r.StartsWith("linux") => $"Linux ({r})",
    var r when r.StartsWith("osx") => $"macOS ({r})",
    _ => rid,
};

// ---------------------------------------------------------------- 工具检查
void Require(string exe, string hint)
{
    if (!OnPath(exe))
    {
        Console.Error.WriteLine($"错误：未找到 {exe}。{hint}");
        Environment.Exit(1);
    }
}

static bool OnPath(string exe)
{
    var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    // Windows 上 PATHEXT 通常含 .EXE，需一并尝试
    string[] names = isWindows && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? new[] { exe, exe + ".exe" }
        : new[] { exe };

    var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
    return paths.Split(Path.PathSeparator)
        .Any(dir => !string.IsNullOrWhiteSpace(dir)
                 && names.Any(n => File.Exists(Path.Combine(dir, n))));
}

Require("dotnet", "请安装 .NET 8 SDK：https://dotnet.microsoft.com/download");

// git 可选：仅打包时提示
bool hasGit = OnPath("git");

// ---------------------------------------------------------------- 主流程
Say($"仓库根目录: {root}");
Say($"项目文件:   {Path.GetFileName(projectFile)}");
Say($"配置:       {configuration}");
Say($"自包含:     {(selfContained ? "是（无需预装 .NET）" : "否（依赖 .NET 8 运行时）")}");
Say($"平台数量:   {selected.Count()}");
Console.WriteLine();

if (clean && Directory.Exists(artifacts))
{
    Say("清理 artifacts/ …");
    Directory.Delete(artifacts, true);
}
Directory.CreateDirectory(artifacts);

// ---------------------------------------------------------------- 还原
Say("还原 NuGet 包 …");
if (Run("dotnet", new[] { "restore", projectFile }, root) != 0)
{
    Console.Error.WriteLine("还原失败，终止。");
    Environment.Exit(1);
}

// ---------------------------------------------------------------- 逐平台打包
var results = new List<(string Rid, string Name, bool Ok, string Dir, string Err)>();

foreach (var (rid, name, _) in selected)
{
    Say($"── 打包 {name} ({rid}) ──");
    string outDir = Path.Combine(artifacts, rid);

    var argsList = new List<string>
    {
        "publish", projectFile,
        "-c", configuration,
        "-r", rid,
        "-o", outDir,
        "--nologo",
    };
    if (selfContained) argsList.Add("-p:SelfContained=true");

    int code = Run("dotnet", argsList.ToArray(), root);
    if (code != 0)
    {
        Say($"  ✗ 失败（退出码 {code}）");
        results.Add((rid, name, false, outDir, $"退出码 {code}"));
        continue;
    }

    // 校验主程序是否真的产出了
    string exeName = rid.StartsWith("win") ? "ColorMod.exe" : "ColorMod";
    string exePath = Path.Combine(outDir, exeName);
    if (!File.Exists(exePath))
    {
        Say("  ✗ 未找到主程序产物");
        results.Add((rid, name, false, outDir, "缺少主程序"));
        continue;
    }

    // 校验前端资源随包发布（Photino 依赖它）
    bool hasIndex = File.Exists(Path.Combine(outDir, "wwwroot", "index.html"));
    bool hasPalettes = File.Exists(Path.Combine(outDir, "wwwroot", "data", "default_palettes.json"));

    long size = new DirectoryInfo(outDir).EnumerateFiles()
        .Sum(f => { try { return f.Length; } catch { return 0L; } });

    string mb = (size / 1024.0 / 1024.0).ToString("F1");
    if (!hasIndex) Say("  ⚠ 缺少 wwwroot/index.html（程序会启动失败）");
    if (!hasPalettes) Say("  ⚠ 缺少内置色卡 JSON");

    Say($"  ✓ 完成 {mb} MB{(hasIndex ? "" : "（⚠ 资源不全）")}");
    results.Add((rid, name, true, outDir, ""));
}

// ---------------------------------------------------------------- 分发格式
// zip            : 通用压缩包（所有平台）
// setup          : Windows 自解压安装程序（单 exe，双击自动安装并运行）
// dmg            : macOS 磁盘映像（需 macOS 环境 + hdiutil）
// deb/rpm        : Linux 安装包（分别对应 Debian/Ubuntu 与 Fedora/RHEL 系）
var madeZips = new List<string>();
var madeSetup = new List<string>();
var madeDmg = new List<string>();
var madeLinux = new List<string>();

// ---- zip：所有平台通用 ----
if (makeZip)
{
    Say("生成 zip 压缩包 …");
    foreach (var (rid, _, ok, dir, _) in results.Where(r => r.Ok))
    {
        string zip = Path.Combine(artifacts, $"ColorMod-{rid}-{configuration.ToLowerInvariant()}.zip");
        try
        {
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, false);
            double mb = new FileInfo(zip).Length / 1024.0 / 1024.0;
            madeZips.Add(Path.GetFileName(zip));
            Say($"  ✓ {Path.GetFileName(zip)}  ({mb:F1} MB)");
        }
        catch (Exception ex) { Say($"  ✗ zip 失败 {rid}: {ex.Message}"); }
    }
}

// ---- Windows 自解压安装程序 ----
// 做法：把 publish 输出做成「Payload 目录」压缩包，再并入一个能自解压的启动壳。
// 启动壳是 C# 单文件程序：定位自身目录的 payload.zip -> 解压到 %LOCALAPPDATA% -> 启动 ColorMod.exe
if (makeSetup)
{
    foreach (var (rid, _, ok, dir, _) in results.Where(r => r.Ok && r.Rid.StartsWith("win")))
    {
        Say($"生成 Windows 安装程序（自解压）…");
        string staging = Path.Combine(artifacts, "_setup_tmp", rid);
        string setupOut = Path.Combine(artifacts, $"ColorMod-Setup-{rid}.exe");

        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            CopyDirectory(dir, staging);

            // 写入安装信息与卸载脚本
            File.WriteAllText(Path.Combine(staging, "install.json"),
                JsonSerializer.Serialize(new
                {
                    product = "ColorMod 色卡工坊",
                    version = ReadVersion(),
                    rid,
                    installedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            string launchCmd = Path.Combine(staging,
                rid.EndsWith("arm64") ? "ColorMod.exe" : "ColorMod.exe");
            File.WriteAllText(Path.Combine(staging, "安装说明.txt"),
                "ColorMod 色卡工坊\r\n" +
                "====================\r\n\r\n" +
                $"版本：{ReadVersion()}\r\n" +
                $"平台：{rid}\r\n" +
                $"安装时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n\r\n" +
                "运行方式：双击 ColorMod.exe\r\n" +
                "卸载方式：删除本文件夹即可\r\n" +
                $"数据目录：{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\ColorCardStudio\r\n",
                Encoding.UTF8);

            // 压缩为 payload.zip
            string payload = Path.Combine(artifacts, "_setup_tmp", $"payload-{rid}.zip");
            if (File.Exists(payload)) File.Delete(payload);
            ZipFile.CreateFromDirectory(staging, payload, CompressionLevel.Optimal, false);

            // 用仓库内的安装壳模板，把 payload.zip 作为嵌入资源打进单文件 exe
            if (!File.Exists(LauncherProject))
            {
                Say("  ✗ 缺少安装壳模板 build/SetupLauncher/SetupLauncher.csproj");
            }
            else
            {
                string launcherOut = Path.Combine(artifacts, "_setup_tmp", "launcher_" + rid);
                var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = root };
                foreach (var a in new[]
                {
                    "publish", LauncherProject,
                    "-c", configuration,
                    "-r", rid,
                    "-o", launcherOut,
                    "-p:PayloadZip=" + payload,
                    "--nologo",
                }) psi.ArgumentList.Add(a);
                int rc = RunPsi(psi);

                string built = Path.Combine(launcherOut, "ColorModSetup.exe");
                if (rc == 0 && File.Exists(built))
                {
                    if (File.Exists(setupOut)) File.Delete(setupOut);
                    File.Copy(built, setupOut, true);
                    double mb = new FileInfo(setupOut).Length / 1024.0 / 1024.0;
                    madeSetup.Add(Path.GetFileName(setupOut));
                    Say($"  ✓ {Path.GetFileName(setupOut)}  ({mb:F1} MB，双击即安装)");
                }
                else Say($"  ✗ 安装壳生成失败（退出码 {rc}）");
            }
        }
        catch (Exception ex) { Say($"  ✗ 安装程序失败 {rid}: {ex.Message}"); }
    }
}

// ---- macOS dmg ----
if (makeDmg)
{
    bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    var macTargets = results.Where(r => r.Ok && r.Rid.StartsWith("osx")).ToList();
    if (!macTargets.Any()) Say("跳过 dmg：本次未打包 macOS 平台");
    else if (!isMac) Say("跳过 dmg：需在 macOS 上执行本脚本（依赖系统自带 hdiutil）");
    else if (!OnPath("hdiutil")) Say("跳过 dmg：未找到 hdiutil");
    else
    {
        foreach (var (rid, _, _, dir, _) in macTargets)
        {
            string dmg = Path.Combine(artifacts, $"ColorMod-{rid}.dmg");
            try
            {
                Say($"生成 dmg（{rid}）…");
                var psi = new ProcessStartInfo("hdiutil") { WorkingDirectory = root };
                foreach (var a in new[]
                {
                    "create", "-volname", "ColorMod", "-srcfolder", dir,
                    "-ov", "-format", "UDZO", dmg,
                }) psi.ArgumentList.Add(a);
                if (RunPsi(psi) == 0)
                {
                    double mb = new FileInfo(dmg).Length / 1024.0 / 1024.0;
                    madeDmg.Add(Path.GetFileName(dmg));
                    Say($"  ✓ {Path.GetFileName(dmg)}  ({mb:F1} MB)");
                }
            }
            catch (Exception ex) { Say($"  ✗ dmg 失败 {rid}: {ex.Message}"); }
        }
    }
}

// ---- Linux deb / rpm ----
if (makeLinuxPkg)
{
    var linuxTargets = results.Where(r => r.Ok && r.Rid.StartsWith("linux")).ToList();
    if (!linuxTargets.Any()) Say("跳过 deb/rpm：本次未打包 Linux 平台");
    else
    {
        foreach (var (rid, _, _, dir, _) in linuxTargets)
        {
            bool arm = rid.EndsWith("arm64");
            string arch = arm ? "arm64" : "amd64";
            string pkgName = $"colormod_{ReadVersion()}_{arch}";

            // 统一的安装布局（遵循 FHS 目录规范）
            string stage = Path.Combine(artifacts, "_linux_tmp", rid);
            try
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                string optRoot = Path.Combine(stage, "opt", "colormod");
                Directory.CreateDirectory(optRoot);
                CopyDirectory(dir, optRoot);

                string binDir = Path.Combine(stage, "usr", "bin");
                Directory.CreateDirectory(binDir);
                var entry = Path.Combine(binDir, "colormod");
                File.WriteAllText(entry,
                    "#!/bin/sh\n" +
                    $"exec /opt/colormod/ColorMod \"$@\"\n", new UTF8Encoding(false));

                string iconDir = Path.Combine(stage, "usr", "share", "icons", "hicolor", "256x256", "apps");
                Directory.CreateDirectory(iconDir);
                var ico = Path.Combine(root, "app.ico");
                if (File.Exists(ico)) File.Copy(ico, Path.Combine(iconDir, "colormod.ico"), true);

                File.WriteAllText(Path.Combine(stage, "control"), BuildDebControl(arch),
                    new UTF8Encoding(false));
                // deb 规范要求 debian-binary 内容为 "2.0\n"
                File.WriteAllText(Path.Combine(stage, "debian-binary"), "2.0\n");
                string dataTar = Path.Combine(stage, "data.tar.gz");
                if (File.Exists(dataTar)) File.Delete(dataTar);
                TarDirectory(stage, dataTar, new[] { "./opt", "./usr" });

                // control.tar.gz：单文件 gzip tar
                string controlTar = Path.Combine(stage, "control.tar.gz");
                if (File.Exists(controlTar)) File.Delete(controlTar);
                BuildGzipTar(Path.Combine(stage, "control"), controlTar);

                // deb = ar 归档，用纯 C# 生成（不依赖 binutils 的 ar）
                string deb = Path.Combine(artifacts, pkgName + ".deb");
                if (File.Exists(deb)) File.Delete(deb);
                BuildArArchive(deb, new[]
                {
                    ("debian-binary", Path.Combine(stage, "debian-binary")),
                    ("control.tar.gz", controlTar),
                    ("data.tar.gz",  dataTar),
                });

                {
                    double mb = new FileInfo(deb).Length / 1024.0 / 1024.0;
                    madeLinux.Add(Path.GetFileName(deb));
                    Say($"  ✓ {Path.GetFileName(deb)}  ({mb:F1} MB)");
                }
            }
            catch (Exception ex) { Say($"  ✗ Linux 打包失败 {rid}: {ex.Message}"); }
        }
    }
}

// ---------------------------------------------------------------- 平台说明
Say("生成平台运行时说明 …");
var notes = new StringBuilder();
notes.AppendLine("# ColorMod 运行时依赖");
notes.AppendLine();
notes.AppendLine("| 平台 | 需要预装 | WebView 依赖 |");
notes.AppendLine("| --- | --- | --- |");
notes.AppendLine("| Windows x64 / ARM64 | Windows 10 1809+（含 WebView2） | Edge WebView2 Runtime（Win11 已内置） |");
notes.AppendLine("| Linux x64 / ARM64 | .NET 8 运行时（自包含包免装） | libwebkit2gtk-4.0-37 及 GTK3 |");
notes.AppendLine("| macOS Intel / Apple Silicon | macOS 11+ | 系统自带 WebKit |");
notes.AppendLine();
notes.AppendLine("Debian/Ubuntu 安装 WebKit：");
notes.AppendLine("```bash");
notes.AppendLine("sudo apt install libwebkit2gtk-4.0-37 libgtk-3-0");
notes.AppendLine("```");
notes.AppendLine();
notes.AppendLine("## 安装包格式");
notes.AppendLine();
notes.AppendLine("| 格式 | 文件 | 说明 |");
notes.AppendLine("| --- | --- | --- |");
notes.AppendLine("| Windows 安装程序 | `ColorMod-Setup-win-x64.exe` | 自解压单文件，双击即安装到 `%LOCALAPPDATA%\\Programs\\ColorMod` 并启动 |");
notes.AppendLine("| zip | `ColorMod-<rid>-release.zip` | 解压后运行目录内的 `ColorMod` / `ColorMod.exe` |");
notes.AppendLine("| deb | `colormod_<版本>_<arch>.deb` | `sudo dpkg -i` 安装，命令行 `colormod` 启动 |");
notes.AppendLine("| dmg | `ColorMod-<rid>.dmg` | 需在 macOS 上构建 |");
notes.AppendLine();
notes.AppendLine("卸载：删除安装目录即可。预设与日志不在安装目录内，保留在用户数据目录。");
notes.AppendLine();
notes.AppendLine("若 WebView 缺失，程序会启动失败并写日志：");
notes.AppendLine("`~/.config/colormod/logs/`（Linux/macOS）或 `%APPDATA%\\ColorCardStudio\\logs\\`（Windows）");
File.WriteAllText(Path.Combine(artifacts, "PLATFORMS.md"), notes.ToString(), new UTF8Encoding(false));

// ---------------------------------------------------------------- 汇总
Console.WriteLine();
Say("打包汇总");
Console.WriteLine(new string('─', 62));
foreach (var (rid, name, ok, dir, err) in results)
    Console.WriteLine($"  {(ok ? "✓" : "✗")}  {name,-22} {rid,-14} {(err == "" ? Relative(dir) : err)}");
Console.WriteLine(new string('─', 62));

// 分发格式清单
var dist = new List<(string Kind, List<string> Files)>
{
    ("zip 压缩包", madeZips),
    ("Windows 安装程序", madeSetup),
    ("macOS dmg", madeDmg),
    ("Linux deb", madeLinux),
};
var anyDist = dist.Where(d => d.Files.Count > 0).ToList();
if (anyDist.Count > 0)
{
    Console.WriteLine();
    Say("分发产物");
    foreach (var (kind, files) in anyDist)
    {
        Console.WriteLine($"  {kind}");
        foreach (var f in files) Console.WriteLine($"    · {f}");
    }
    Console.WriteLine(new string('─', 62));
}

int failed = results.Count(r => !r.Ok);
Say(failed == 0
    ? $"全部 {results.Count} 个平台打包完成 -> {artifacts}"
    : $"{results.Count - failed}/{results.Count} 成功，{failed} 个失败");

if (hasGit && results.Any(r => r.Ok))
    Say("提示：源码与构建脚本已就绪，可提交到版本库");


// ---------------------------------------------------------------- 辅助实现（分发）

static string ReadVersion()
{
    var csproj = Directory.EnumerateFiles(Environment.CurrentDirectory, "ColorCardStudio.csproj", SearchOption.AllDirectories)
        .FirstOrDefault();
    if (csproj != null)
    {
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(csproj),
            @"<Version>([\d.]+)</Version>");
        if (m.Success) return m.Groups[1].Value;
    }
    return "1.0.0";
}

static void CopyDirectory(string src, string dst)
{
    Directory.CreateDirectory(dst);
    foreach (var f in Directory.EnumerateFiles(src))
        File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
    foreach (var d in Directory.EnumerateDirectories(src))
        CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
}

/// <summary>自解压安装壳项目路径（仓库内预置模板）。</summary>
string LauncherProject => Path.Combine(root, "build", "SetupLauncher", "SetupLauncher.csproj");

/// <summary>生成 deb 包的 control 文件内容。</summary>
static string BuildDebControl(string arch)
{
    var ver = ReadVersion();
    var size = "10240";   // 1MiB（安装后大小由 dpkg 计算，此处仅为初值）
    return string.Join("\n", new[]
    {
        "Package: colormod",
        $"Version: {ver}",
        "Section: graphics",
        "Priority: optional",
        $"Architecture: {arch}",
        "Maintainer: ColorMod <dev@colormod.local>",
        $"Installed-Size: {size}",
        "Depends: libc6, libwebkit2gtk-4.0-37, libgtk-3-0",
        "Description: CMOK 色卡工坊（基于 Photino.NET 的桌面色卡查询与图片取色工具）",
        " 支持印刷标准换算、色卡分类检索与预设管理。",
        "",
    });
}

/// <summary>把若干目录打成 ustar 归档（tar 格式，GNU/BSD 通用）。</summary>
static void TarDirectory(string rootDir, string outTar, string[] prefixes)
{
    using var fs = File.Create(outTar);
    using var gz = new GZipStream(fs, CompressionLevel.Optimal);
    WriteTar(gz, rootDir, prefixes);
}

static void WriteTar(Stream outStream, string rootDir, string[] prefixes)
{
    foreach (var prefix in prefixes)
    {
        var full = Path.Combine(rootDir, prefix.TrimStart('.', '/', '\\')
            .Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(full)) continue;

        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            string rel = "./" + Path.GetRelativePath(rootDir, file).Replace('\\', '/');
            var info = new FileInfo(file);
            WriteTarHeader(outStream, rel, info.Length, 0b111_111_111);
            using var f = File.OpenRead(file);
            f.CopyTo(outStream);
            long pad = (512 - info.Length % 512) % 512;
            for (long i = 0; i < pad; i++) outStream.WriteByte(0);
        }
    }
    // 两个空块表示结束
    for (int i = 0; i < 1024; i++) outStream.WriteByte(0);
}

static void WriteTarHeader(Stream s, string name, long size, int mode)
{
    var header = new byte[512];
    void Put(int off, string val, int len)
    {
        var b = Encoding.ASCII.GetBytes(val);
        Array.Copy(b, 0, header, off, Math.Min(b.Length, len - 1));
    }
    Put(0, name, 100);
    Put(100, "0000644", 8);            // mode
    Put(108, "0000000", 8);            // uid
    Put(116, "0000000", 8);            // gid
    Put(124, size.ToString("11") + "\0", 12);
    Put(136, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("11") + "\0", 12);
    for (int i = 148; i < 156; i++) header[i] = (byte)' ';
    header[156] = (byte)'0';            // 普通文件
    Put(257, "ustar", 6);
    Put(263, "00", 2);
    s.Write(header, 0, header.Length);
    // 校验和：填入空格后再算
    int sum = header.Sum(b => b);
    Put(148, sum.ToString("6") + "\0 ", 8);
    s.Write(header, 148, 8);
}

/// <summary>单个文件打成 tar.gz（deb 的 control.tar.gz）。</summary>
static void BuildGzipTar(string file, string outTar)
{
    using var fs = File.Create(outTar);
    using var gz = new GZipStream(fs, CompressionLevel.Optimal);
    string name = "./" + Path.GetFileName(file);
    var info = new FileInfo(file);
    WriteTarHeader(gz, name, info.Length, 0b111_111_111);
    using (var f = File.OpenRead(file)) f.CopyTo(gz);
    long pad = (512 - info.Length % 512) % 512;
    for (long i = 0; i < pad; i++) gz.WriteByte(0);
    for (int i = 0; i < 1024; i++) gz.WriteByte(0);
}


return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------- 辅助实现

int Run(string exe, string[] args, string cwd)
{
    var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    return RunPsi(psi);
}

int Run(string exe, string singleArg, string cwd)
{
    var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd };
    psi.ArgumentList.Add(singleArg);
    return RunPsi(psi);
}

int RunPsi(ProcessStartInfo psi)
{
    try
    {
        using var p = Process.Start(psi);
        if (p == null) return 1;
        p.WaitForExit();
        return p.ExitCode;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  执行失败: {ex.Message}");
        return 1;
    }
}

string Relative(string path)
{
    try { return Path.GetRelativePath(root, path); }
    catch { return path; }
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ColorCardStudio.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // 回退：从脚本自身位置上溯（build/ 的上一级）
    var scriptDir = Path.GetDirectoryName(GetScriptPath());
    var guess = new DirectoryInfo(scriptDir).Parent;
    return guess?.FullName ?? Directory.GetCurrentDirectory();
}

static string GetScriptPath()
{
    // dotnet-script 会把脚本路径放在环境变量或命令行中
    foreach (var a in Environment.GetCommandLineArgs())
        if (a.EndsWith(".csx", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
            return a;
    var dir = AppContext.BaseDirectory;
    return Directory.EnumerateFiles(dir, "*.csx", SearchOption.AllDirectories).FirstOrDefault() ?? "";
}

/// <summary>
/// 生成 ar 归档（deb 的外壳格式）。纯 C# 实现，无需 binutils。
/// 成员顺序必须是 debian-binary、control.tar.gz、data.tar.gz。
/// </summary>
static void BuildArArchive(string outPath, (string Name, string File)[] members)
{
    using var fs = File.Create(outPath);
    using var w = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

    const string arMagic = "!<arch>\n";
    w.Write(Encoding.ASCII.GetBytes(arMagic));

    foreach (var (name, file) in members)
    {
        var info = new FileInfo(file);
        // 成员头固定 60 字节：名称16 时间12 uid6 gid6 mode8 size10 magic2
        var header = new byte[60];
        for (int i = 0; i < header.Length; i++) header[i] = (byte)' ';

        var nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, 0, header, 0, Math.Min(16, nameBytes.Length));

        void Put(int off, string val, int len)
        {
            var b = Encoding.ASCII.GetBytes(val);
            int n = Math.Min(b.Length, len);
            Array.Copy(b, 0, header, off, n);
            for (int i = n; i < len; i++) header[off + i] = (byte)' ';
        }

        Put(16, info.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), 12);
        Put(28, "0", 6);      // uid
        Put(34, "0", 6);      // gid
        Put(40, "100644", 8);  // mode
        Put(48, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), 10);
        Put(58, "\x60\x0A", 2);   // magic "`\n"

        w.Write(header);
        using (var f = File.OpenRead(file)) f.CopyTo(fs);

        // ar 成员按 2 字节对齐
        long pad = info.Length % 2;
        if (pad == 1) w.Write((byte)'\n');
    }
}
