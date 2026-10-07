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
bool clean = flag("clean");
string configuration = opt("configuration") ?? "Release";

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

// ---------------------------------------------------------------- 可选 zip
if (makeZip)
{
    Say("生成压缩包 …");
    foreach (var (rid, name, ok, dir, _) in results.Where(r => r.Ok))
    {
        string zip = Path.Combine(artifacts, $"ColorMod-{rid}-{configuration}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        try
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(
                dir, zip, System.IO.Compression.CompressionLevel.Optimal, false);
            long zs = new FileInfo(zip).Length;
            Say($"  ✓ {Path.GetFileName(zip)} ({zs / 1024.0 / 1024.0:F1} MB)");
        }
        catch (Exception ex)
        {
            Say($"  ✗ 压缩失败：{ex.Message}");
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

int failed = results.Count(r => !r.Ok);
Say(failed == 0
    ? $"全部 {results.Count} 个平台打包完成 -> {artifacts}"
    : $"{results.Count - failed}/{results.Count} 成功，{failed} 个失败");

if (hasGit && results.Any(r => r.Ok))
    Say("提示：源码与构建脚本已就绪，可提交到版本库");

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
