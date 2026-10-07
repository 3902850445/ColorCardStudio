#!/usr/bin/env dotnet-script
// =============================================================================
// ColorMod 推送 + 触发云构建
//
// 用法：
//   dotnet-script build/push.csx                          # 推送并触发构建
//   dotnet-script build/push.csx -- --skip-build          # 只推送，不触发构建
//   dotnet-script build/push.csx -- --retries 5            # 推送重试次数（默认 3）
//   dotnet-script build/push.csx -- --timeout 300          # 每次尝试超时秒数（默认 120）
//   dotnet-script build/push.csx -- --dry-run              # 只看将推送什么
//   dotnet-script build/push.csx -- --ssh                  # 走 SSH 通道
//   dotnet-script build/push.csx -- --proxy http://127.0.0.1:3066   # 指定代理端口
//   COLORPROD_PROXY=http://127.0.0.1:3066 dotnet-script build/push.csx
//
// 设计要点：
//   1. 全程实时输出状态（每步打时间戳），长时间无输出会自动打印「仍在进行」心跳，
//      避免看起来像卡死。
//   2. 推送失败自动重试，并诊断失败原因（网络 / 认证 / 仓库不存在）。
//   3. 支持 SSH 通道作为 HTTPS 的备选。
//   4. 令牌从 github_key.txt 读取，绝不写入 .git/config。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

try { Console.OutputEncoding = Encoding.UTF8; } catch { }

// SSH 私钥路径（RunWithSshKey 会引用，故需先于该函数声明）
string sshKeyPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519_github");

// ---------------------------------------------------------------- 参数
string[] rawArgs = ReadScriptArgs();

static string[] ReadScriptArgs()
{
    var all = Environment.GetCommandLineArgs();
    int sep = Array.IndexOf(all, "--");
    if (sep >= 0 && sep + 1 < all.Length) return all.Skip(sep + 1).ToArray();
    return all.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray();
}

bool hasFlag(string n) =>
    rawArgs.Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));

string optOf(string n, string dflt = "")
{
    for (int i = 0; i < rawArgs.Length - 1; i++)
        if (rawArgs[i].Equals("--" + n, StringComparison.OrdinalIgnoreCase))
            return rawArgs[i + 1];
    return dflt;
}

int retries = int.TryParse(optOf("retries", "3"), out var r) ? Math.Max(1, r) : 3;
int timeoutSec = int.TryParse(optOf("timeout", "120"), out var t) ? Math.Max(30, t) : 120;
bool skipBuild = hasFlag("skip-build");
bool dryRun = hasFlag("dry-run");
bool useSsh = hasFlag("ssh");
string proxyOverride = optOf("proxy", "");

// ---------------------------------------------------------------- 状态输出
var stepNo = 0;
var startAt = DateTime.Now;

void Say(string level, string msg)
{
    stepNo++;
    var t = DateTime.Now.ToString("HH:mm:ss");
    var bar = new string('─', Math.Min(58, 20 + msg.Length));
    Console.WriteLine($"[{t}] {level,-4} │ {msg}");
    if (level == "STEP") Console.WriteLine($"         └ {bar}");
}

void Step(string msg) => Say("STEP", msg);
void Info(string msg) => Say("INFO", msg);
void Ok(string msg) => Say(" OK ", msg);
void Warn(string msg) => Say("WARN", msg);
void Err(string msg) => Say("FAIL", msg);

/// <summary>执行外部命令，实时转发输出，并打印心跳防止误认为卡死。</summary>
// ---------------------------------------------------------------- 代理探测
// Karing 等本地代理客户端会监听若干端口，其中一个是 HTTP 代理。
// 这里自动探测，避免硬编码端口号（版本升级会变）。

string detectedProxy = "";

/// <summary>依次探测候选端口，返回第一个能代理 GitHub 的 HTTP 代理地址。</summary>
string DetectProxy(bool verbose)
{
    // 优先用环境变量，其次用自动探测
    string fromEnv = Environment.GetEnvironmentVariable("COLORPROD_PROXY")
                     ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
    if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

    int[] candidates = { 3066, 3067, 7890, 7897, 1080, 10809, 20171 };
    if (verbose) Info($"正在探测本地代理（候选端口：{string.Join(", ", candidates)}）…");

    foreach (int port in candidates)
    {
        string url = $"http://127.0.0.1:{port}";
        var (_, outp) = Run("curl",
            new[] { "-s", "-m", "8", "-x", url, "-w", "\nHTTP:%{http_code}",
                    "https://api.github.com/rate_limit" },
            15, echo: false);
        if (outp.Contains("HTTP:200"))
        {
            if (verbose) Ok($"发现可用代理：{url}");
            return url;
        }
    }
    return null;
}

/// <summary>把探测到的代理写入子进程环境变量。</summary>
void ApplyProxy(ProcessStartInfo psi)
{
    if (string.IsNullOrEmpty(detectedProxy)) return;
    psi.Environment["HTTPS_PROXY"] = detectedProxy;
    psi.Environment["HTTP_PROXY"] = detectedProxy;
    psi.Environment["https_proxy"] = detectedProxy;
    psi.Environment["http_proxy"] = detectedProxy;
    // git 需显式允许代理（部分版本默认不读环境变量）
    psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
}

(int code, string output) Run(string file, IEnumerable<string> args, int timeoutSeconds, bool echo = true)
{
    var psi = new ProcessStartInfo(file)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    ApplyProxy(psi);

    using var proc = new Process { StartInfo = psi };
    var sb = new StringBuilder();

    void Drain(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (sb)
        {
            sb.AppendLine(line);
            if (echo) Console.WriteLine("      \u2502 " + line);
        }
    }

    proc.OutputDataReceived += (_, e) => Drain(e.Data ?? string.Empty);
    proc.ErrorDataReceived += (_, e) => Drain(e.Data ?? string.Empty);

    proc.Start();
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();

    var runStart = DateTime.Now;
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    var lastBeat = DateTime.UtcNow;
    while (!proc.HasExited)
    {
        Thread.Sleep(300);
        if (DateTime.UtcNow > deadline)
        {
            Warn($"超过 {timeoutSeconds} 秒仍未结束，终止该进程");
            try { proc.Kill(true); } catch { }
            break;
        }
        // 心跳：每 10 秒提示一次仍在进行
        if (DateTime.UtcNow - lastBeat > TimeSpan.FromSeconds(10))
        {
            lastBeat = DateTime.UtcNow;
            var run = (DateTime.Now - runStart).TotalSeconds;
            Info($"仍在进行… 已耗时 {run:F0} 秒（进程 pid={proc.Id}）");
        }
    }

    proc.WaitForExit(5000);
    return (proc.ExitCode, sb.ToString());
}

/// <summary>
/// 同 Run，但通过 GIT_SSH_COMMAND 指定专用私钥。
/// 避免启动 ssh-agent（非交互环境下会阻塞等待）。
/// </summary>
(int code, string output) RunWithSshKey(string file, IEnumerable<string> args, int timeoutSeconds)
{
    var psi = new ProcessStartInfo(file)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);

    // 显式指定私钥与已知主机，避开交互提示与主机密钥确认
    psi.Environment["GIT_SSH_COMMAND"] =
        "ssh -i \"" + sshKeyPath + "\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o ConnectTimeout=25";
    ApplyProxy(psi);

    using var proc = new Process { StartInfo = psi };
    var sb = new StringBuilder();

    void Drain(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (sb)
        {
            sb.AppendLine(line);
            Console.WriteLine("      \u2502 " + line);
        }
    }

    proc.OutputDataReceived += (_, e) => Drain(e.Data ?? string.Empty);
    proc.ErrorDataReceived += (_, e) => Drain(e.Data ?? string.Empty);

    proc.Start();
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();

    var runStart = DateTime.Now;
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    var lastBeat = DateTime.UtcNow;
    while (!proc.HasExited)
    {
        Thread.Sleep(300);
        if (DateTime.UtcNow > deadline)
        {
            Warn($"超过 {timeoutSeconds} 秒仍未结束，终止");
            try { proc.Kill(true); } catch { }
            break;
        }
        if (DateTime.UtcNow - lastBeat > TimeSpan.FromSeconds(10))
        {
            lastBeat = DateTime.UtcNow;
            Info($"SSH 仍在进行… 已耗时 {(DateTime.Now - runStart).TotalSeconds:F0} 秒（pid={proc.Id}）");
        }
    }

    proc.WaitForExit(5000);
    return (proc.ExitCode, sb.ToString());
}

// ---------------------------------------------------------------- 环境
var root = FindRepoRoot();
string keyFile = FindKeyFile();
if (string.IsNullOrEmpty(keyFile)) keyFile = "";
string repoUrl = "https://github.com/3902850445/ColorCardStudio.git";

Console.WriteLine(new string('═', 64));
Info("ColorMod 推送脚本");
Info($"仓库根目录：{root}");
Info($"远程仓库：  {repoUrl}");
Info($"重试次数：  {retries}    单次超时：{timeoutSec} 秒");
Console.WriteLine(new string('═', 64));
Console.WriteLine();

// 1) 确认 git 与项目
Step("检查前置条件");

var (gitVer, _) = Run("git", new[] { "--version" }, 20);
if (gitVer != 0) { Err("未找到 git，请先安装"); return 1; }

var project = Path.Combine(root, "ColorCardStudio.csproj");
if (!File.Exists(project))
{
    Err($"未找到 {project}");
    return 1;
}
Ok("git 与项目文件就绪");

// 1.5) SSH 通道（如指定 --ssh）
string sshUrl = "git@github.com:3902850445/ColorCardStudio.git";
if (useSsh)
{
    Step("准备 SSH 通道");
    var sshDir = Path.GetDirectoryName(sshKeyPath)!;
    Directory.CreateDirectory(sshDir);

    if (!File.Exists(sshKeyPath))
    {
        Info("未检测到专用 SSH 密钥，正在生成 …");
        var (kCode, kOut) = Run("ssh-keygen",
            new[] { "-t", "ed25519", "-f", sshKeyPath, "-N", "", "-C", "colormod-github" }, 60);
        if (kCode != 0)
        {
            Err("密钥生成失败：" + kOut.Trim());
            return 1;
        }
        Ok("已生成密钥对");
    }
    else Ok("已存在专用密钥");

    string pub = sshKeyPath + ".pub";
    if (!File.Exists(pub))
    {
        Err("找不到公钥文件 " + pub);
        return 1;
    }

    // 不启动 ssh-agent（非交互 shell 会阻塞）；改用 GIT_SSH_COMMAND 指定私钥
    Info("已指定专用私钥，无需 ssh-agent");

    Console.WriteLine();
    Warn("首次使用需把下面这行公钥添加到 GitHub：");
    Info("  仓库 Settings → SSH and GPG keys → New SSH key");
    Console.WriteLine();
    Console.WriteLine("      " + File.ReadAllText(pub).Trim());
    Console.WriteLine();
    Info("添加后重新运行本脚本即可。");
    if (dryRun) return 0;
}

// 2) 读取令牌
Step("读取访问令牌");
if (keyFile == null)
{
    Err("未找到 github_key.txt");
    Info("请在仓库根目录或工作区创建 github_key.txt，格式：");
    Info("  用户名：xxx");
    Info("  密码：github_pat_xxxxxxxx");
    return 1;
}

string user = "", token = "";
foreach (var raw in File.ReadAllLines(keyFile))
{
    var m = Regex.Match(raw, @"用户名[：:]\s*(\S+)");
    if (m.Success) { user = m.Groups[1].Value; continue; }
    m = Regex.Match(raw, @"密码[：:]\s*(\S+)");
    if (m.Success) token = m.Groups[1].Value;
}

if (string.IsNullOrWhiteSpace(token) && !useSsh)
{
    Err($"未能从 {keyFile} 解析出令牌（HTTPS 模式必需；或改用 --ssh）");
    return 1;
}
Ok(useSsh
    ? "SSH 模式，令牌非必需"
    : $"令牌已读取（用户 {user}，长度 {token.Length}，不显示内容）");

// 3) 检查待推送内容
Step("检查工作区状态");
var (statusCode, statusOut) = Run("git", new[] { "status", "--porcelain" }, 30);
int changed = string.IsNullOrWhiteSpace(statusOut) ? 0 : statusOut.Trim().Split('\n').Length;
if (changed > 0) Info($"检测到 {changed} 个未提交更改");
else Ok("工作区干净");

var (branchCode, branchOut) = Run("git", new[] { "rev-parse", "--abbrev-ref", "HEAD" }, 20, false);
string branch = branchOut.Trim();
Ok($"当前分支：{branch}");

var (logCode, logOut) = Run("git", new[] { "log", "--oneline", "-1" }, 20, false);
Ok($"最新提交：{logOut.Trim()}");

// 确认密钥文件未被跟踪
var (trackedCode, trackedOut) = Run("git", new[] { "ls-files" }, 30, false);
bool keyTracked = trackedOut.Split('\n').Any(f =>
    f.Trim().EndsWith("github_key.txt", StringComparison.OrdinalIgnoreCase)
    || f.Trim().EndsWith("git_key.txt", StringComparison.OrdinalIgnoreCase));
if (keyTracked)
{
    Err("检测到密钥文件被 git 跟踪！这会导致令牌泄漏到远程仓库");
    Info("请执行：git rm --cached github_key.txt 并确认 .gitignore 已包含");
    return 1;
}
Ok("密钥文件未被跟踪（安全）");

if (dryRun)
{
    Console.WriteLine();
    Warn("dry-run 模式，未执行任何推送");
    return 0;
}

// 3.5) 探测本地代理
Console.WriteLine();
Step("探测本地代理");
detectedProxy = !string.IsNullOrEmpty(proxyOverride)
    ? proxyOverride
    : DetectProxy(true);
if (detectedProxy != null)
{
    Ok($"使用代理：{detectedProxy}");
}
else
{
    Warn("未发现可用的本地 HTTP 代理，将直连");
    Info("若使用 Karing 等代理工具，请确认其已启动并开启 HTTP 代理端口");
    Info("也可显式指定：dotnet-script build/push.csx -- --proxy http://127.0.0.1:3066");
}

// 4) 推送（带重试）
Console.WriteLine();
Step($"推送到 GitHub（最多 {retries} 次尝试）");

string authUrl = $"https://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(token)}@github.com/3902850445/ColorCardStudio.git";
string pushUrl = useSsh ? sshUrl : authUrl;
if (useSsh) Info("使用 SSH 通道推送");
bool pushed = false;
string lastOutput = "";

for (int attempt = 1; attempt <= retries && !pushed; attempt++)
{
    Info($"第 {attempt}/{retries} 次尝试…");

    var (code, output) = RunWithSshKey("git",
        new[] { "push", pushUrl, $"{branch}:{branch}" },
        timeoutSec);

    lastOutput = output;

    if (code == 0 && !Regex.IsMatch(output, @"(?im)^\s*(fatal|error)"))
    {
        Ok("推送成功");
        pushed = true;
        break;
    }

    // 失败诊断
    string reason =
        Regex.IsMatch(output, @"CONNECT tunnel failed|TLS|SSL|certificate|timed out|Failed to connect", RegexOptions.IgnoreCase)
            ? "网络不通（代理/TLS/超时）——建议检查代理设置、改用 SSH，或换网络环境"
        : Regex.IsMatch(output, @"Authentication failed|could not read Username|403|401", RegexOptions.IgnoreCase)
            ? "认证失败——请确认令牌有效且具备 repo 权限与 workflow 写权限"
        : Regex.IsMatch(output, @"Repository not found|404", RegexOptions.IgnoreCase)
            ? "仓库不存在或令牌无权访问——请确认仓库已创建"
        : "未知原因（见上方输出）";

    Warn($"失败原因：{reason}");
    if (attempt < retries)
    {
        int wait = Math.Min(5 * attempt, 15);
        Info($"{wait} 秒后重试…（可加 --retries {retries * 2} 增加次数）");
        Thread.Sleep(wait * 1000);
    }
}

if (!pushed)
{
    Console.WriteLine();
    Err("推送未成功");
    Info("可选方案：");
    Info("  1) 换网络 / 关闭代理后重试");
    Info("  2) 改用 SSH：先在 GitHub 添加公钥，再执行");
    Info("     git remote set-url gh git@github.com:3902850445/ColorCardStudio.git");
    Info("     git push gh " + branch);
    Info("  3) 提高重试：dotnet-script build/push.csx -- --retries 8 --timeout 300");
    if (lastOutput.Length > 0)
    {
        Console.WriteLine();
        Info("最后一次输出：");
        foreach (var line in lastOutput.Trim().Split('\n').TakeLast(8))
            Console.WriteLine("      │ " + line);
    }
    return 2;
}

// 5) 验证远程分支
Console.WriteLine();
Step("验证远程状态");
var (lsCode, lsOut) = useSsh
    ? RunWithSshKey("git", new[] { "ls-remote", pushUrl, $"refs/heads/{branch}" }, 60)
    : Run("git", new[] { "ls-remote", pushUrl, $"refs/heads/{branch}" }, 60, false);
if (lsCode == 0 && lsOut.Trim().Length > 0)
{
    var sha = lsOut.Trim().Split('\t')[0];
    Ok($"远程 {branch} 存在，HEAD = {sha[..Math.Min(8, sha.Length)]}");
}
else
{
    Warn("无法确认远程状态（不影响已完成的推送）");
}

// 6) 触发云构建（可选）
if (!skipBuild && !string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine();
    Step("触发 GitHub Actions 云构建");

    // 调用独立的 PowerShell 模板（避免在 csx 里做多层转义）
    string ps1 = Path.Combine(root, "build", "trigger-workflow.ps1");
    if (!File.Exists(ps1))
    {
        Warn("缺少 build/trigger-workflow.ps1，跳过自动触发");
        Info("手动触发：https://github.com/3902850445/ColorCardStudio/actions");
    }
    else
    {
        var (apiCode, apiOut) = Run("powershell",
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps1,
                    "-Token", token, "-Repo", "3902850445/ColorCardStudio", "-Ref", branch },
            90, false);

        if (apiOut.Contains("DISPATCH_OK"))
        {
            Ok("云构建已触发");
            Info("查看进度：https://github.com/3902850445/ColorCardStudio/actions");
            Info("（首次运行需在 Actions 页面点「I understand my workflows, enable them」）");
        }
        else
        {
            Warn("云构建未能自动触发（不影响代码已推送）");
            foreach (var line in apiOut.Trim().Split('\n').TakeLast(3))
                Info("  " + line);
            Info("可手动触发：仓库 Actions 页面 → 跨平台构建与发布 → Run workflow");
        }
    }
}
else if (skipBuild)
{
    Info("已跳过云构建触发（--skip-build）");
    Info("手动触发：https://github.com/3902850445/ColorCardStudio/actions");
}

// 完成
var total = (DateTime.Now - startAt).TotalSeconds;
Console.WriteLine();
Console.WriteLine(new string('═', 64));
Ok($"全部完成，用时 {total:F0} 秒");
Info("仓库：https://github.com/3902850445/ColorCardStudio");
Console.WriteLine(new string('═', 64));

return 0;

// ---------------------------------------------------------------- 辅助
string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ColorCardStudio.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // 从脚本位置上溯两级（build/ 的上一级）
    var scriptDir = Path.GetDirectoryName(
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
        ?? Environment.CurrentDirectory);
    return new DirectoryInfo(scriptDir).Parent?.FullName ?? Environment.CurrentDirectory;
}

static string FindKeyFile()
{
    var names = new[] { "github_key.txt", "git_key.txt" };
    var dirs = new List<string> { Environment.CurrentDirectory };
    for (int i = 0; i < 5; i++)
    {
        dirs.Add(dirs[^1]);
    }
    foreach (var n in names)
    {
        foreach (var d in dirs.Distinct())
        {
            var p = Path.Combine(d, n);
            if (File.Exists(p)) return p;
        }
    }
    // 兜底：在上层工作区里找
    var up = Directory.GetParent(Environment.CurrentDirectory);
    while (up != null)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(up.FullName, n);
            if (File.Exists(p)) return p;
        }
        up = up.Parent;
    }
    return null;
}