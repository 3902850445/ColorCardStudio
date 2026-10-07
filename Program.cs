using System.Text;
﻿using System.Text.Json;
using System.Text.Json.Serialization;
using ColorCardStudio.Core;
using ColorCardStudio.Tests;
using System.Diagnostics;
using System.Reflection;
using Photino.NET;
using Trace = ColorCardStudio.Core.Trace;
namespace ColorCardStudio;

public static class Program
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 必须显式开启：前端发的是 camelCase（id/action/payload），
        // 而 C# 属性是 PascalCase（Id/Action/Payload）。
        // 缺失此项时反序列化不会报错，只是所有字段静默为 null ——
        // 表现为「请求已收到但没有 action」，极难排查。
        PropertyNameCaseInsensitive = true,
        // 输出同样统一为 camelCase：前端读的是 s.id / s.tacLimit，
        // 若这里输出 PascalCase，前端会拿到 undefined。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static readonly PaletteStore Store = new();
    static readonly ColorExtractor Extractor = new();

    [STAThread]
    public static void Main(string[] args)
    {
        Log.Info("启动", $"ColorMod 启动，参数: {(args.Length == 0 ? "(无)" : string.Join(" ", args))}");
        Log.Info("启动", $"程序目录: {AppContext.BaseDirectory}");

        try
        {
            Log.Cleanup();
            Log.Debug("启动", "旧日志清理完成");
        }
        catch (Exception ex) { Log.Exception("启动", "日志清理失败", ex); }

        // --selftest：核心算法自测（WinExe 无控制台，需先挂接）
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleRunner.Attach();
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        // --fetch-palettes：联网抓取主流色卡并更新内置默认预设
        if (args.Any(a => a.Equals("--fetch-palettes", StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleRunner.Attach();
            Environment.ExitCode = FetchPalettes(args);
            return;
        }

        string? indexPath = ResolveIndex();
        Log.Info("启动", indexPath != null
            ? $"前端入口: {indexPath}"
            : "未找到前端入口 index.html");
        if (indexPath is null)
        {
            // 跨平台提示：WinExe 无控制台，优先弹窗；非 Windows 退化为标准输出
            const string msg = "未找到前端资源 index.html。\n请确认 wwwroot 目录随程序一起发布。";
            Log.Error("启动", msg);
            ShowStartupError(msg);
            return;
        }

        var window = new PhotinoWindow()
            .SetTitle("ColorMod 色卡工坊 — CMYK 色卡查询与取色")
            .SetUseOsDefaultLocation(true)
            .SetSize(1400, 920)
            .SetMinSize(1060, 700)
            .SetResizable(true)
            .SetJavascriptClipboardAccessEnabled(true)
            .SetBrowserControlInitParameters("--remote-debugging-port=9333")
            .SetIgnoreCertificateErrorsEnabled(true);

        // JS -> C#。同时使用注册方法与事件订阅两条路径，
        // 兼容 Photino 4.x 在「窗口创建前 / 创建后」的两种注册模式。
        // 去重键：reqId（前端每次请求都有唯一 id）。
        // 不能按消息内容去重 —— 同一动作（如再次点「选择图片」）内容完全相同，
        // 按内容去重会把用户的正常重复操作吞掉，表现为「卡在解析中」。
        // reqId 相同的才是 Photino 重复投递。
        var seen = new Dictionary<string, long>(64);
        var receivedCount = 0;

        void OnWebMessage(object? sender, string message)
        {
            receivedCount++;
            var now = Environment.TickCount64;

            // 前端发来心跳即代表页面已就绪，此后响应才能真正送达
            if (message.Contains("\"ping\"") || message.Contains("getStandards"))
                MarkWindowReady();

            // 抽取 reqId 作为去重键
            string key = ExtractReqId(message);
            if (key.Length > 0 && seen.TryGetValue(key, out var last) && now - last < 30000)
            {
                Trace.Note("BE", "IN", "去重",
                    $"同一 reqId 重复投递已忽略（{key}，第 {receivedCount} 次收到）");
                return;
            }
            if (key.Length > 0)
            {
                seen[key] = now;
                if (seen.Count > 128)
                {
                    var cutoff = now - 30000;
                    foreach (var k in seen.Where(x => x.Value < cutoff).Select(x => x.Key).ToList())
                        seen.Remove(k);
                }
            }

            Trace.Note("BE", "IN", "WebMessageReceived",
                $"收到前端消息 #{receivedCount}{(key.Length > 0 ? $" ({key})" : "")} ({message.Length} 字符)", message);
            try { _ = HandleAsync(window, message); }
            catch (Exception ex)
            {
                Log.Exception("前端", "消息处理异常", ex);
                Send(window, new { ok = false, error = ex.Message });
            }
        }

        /// <summary>从请求 JSON 中取出 id 字段（仅用于去重，失败返回空串）。</summary>
        static string ExtractReqId(string message)
        {
            const string marker = "\"id\":\"";
            int i = message.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return "";
            int start = i + marker.Length;
            int end = message.IndexOf('"', start);
            return end > start ? message[start..end] : "";
        }

        // 只订阅事件一次。RegisterWebMessageReceivedHandler 与 += 叠加使用会重复挂载。
        window.WebMessageReceivedHandler += OnWebMessage;

        window.RegisterWindowCreatedHandler((s, e) =>
        {
            Log.Info("窗口", "原生窗口已创建");
            Trace.Note("BE", "--", "生命周期", "原生窗口已创建，开始投递心跳");
            StartSender();
            Log.Info("窗口", "消息桥接已就绪（事件已提前订阅）");

            // 页面加载需要时间，周期投递心跳直到前端回应
            int ticks = 0;
            _ = new System.Threading.Timer(_ =>
            {
                if (++ticks > 25) return;
                Trace.Note("BE", "OUT", "心跳", $"投递心跳 #{ticks}");
                Outbound.Enqueue((window, "{\"type\":\"ping\"}"));
            }, null, 500, 500);
        });

        window.Load(indexPath);
        window.Center();
        window.WaitForClose();
    }

    /// <summary>
    /// 启动失败提示。跨平台处理：
    /// Windows 用 MessageBox，其他平台退化为标准输出（GUI 应用通常看不到，故同时记日志）。
    /// </summary>
    static void ShowStartupError(string message)
    {
        Console.Error.WriteLine(message);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ShowWindowsMessageBox(message);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // 借助 osascript 弹窗（若系统支持）
                var psi = new ProcessStartInfo("osascript")
                {
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"display dialog {System.Text.Json.JsonSerializer.Serialize(message)}");
                Process.Start(psi);
            }
            else
            {
                // Linux 桌面：尝试 zenity，缺失则忽略（已写日志）
                var psi = new ProcessStartInfo("zenity")
                {
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--error");
                psi.ArgumentList.Add("--text");
                psi.ArgumentList.Add(message);
                Process.Start(psi);
            }
        }
        catch
        {
            // 提示失败不应阻止进程退出
        }
    }

    /// <summary>
    /// Windows 弹窗。通过反射调用 WinForms，避免项目整体依赖 net8.0-windows。
    /// </summary>
    static void ShowWindowsMessageBox(string message)
    {
        try
        {
            var asm = Assembly.Load("System.Windows.Forms");
            var type = asm.GetType("System.Windows.Forms.MessageBox");
            if (type == null) return;
            var ok = asm.GetType("System.Windows.Forms.MessageBoxButtons")?
                            .GetField("OK")?.GetValue(null);
            var err = asm.GetType("System.Windows.Forms.MessageBoxIcon")?
                             .GetField("Error")?.GetValue(null);
            type.GetMethod("Show", new[] { typeof(string), typeof(string), ok?.GetType() ?? typeof(object), err?.GetType() ?? typeof(object) })
                ?.Invoke(null, new[] { message, "ColorMod", ok!, err! });
        }
        catch
        {
            // 反射失败则退回控制台输出
        }
    }

    /// <summary>定位前端入口文件。</summary>
    static string? ResolveIndex()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "index.html"),
            Path.Combine(AppContext.BaseDirectory, "index.html"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 执行色卡抓取。目标默认写回程序目录下的 wwwroot/data/default_palettes.json；
    /// 开发时若不存在则落到项目目录，避免覆盖发布产物。
    /// </summary>
    static int FetchPalettes(string[] args)
    {
        bool overwrite = args.Any(a => a.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));

        string target = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "default_palettes.json");
        if (!Directory.Exists(Path.GetDirectoryName(target)!))
        {
            string devPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "data", "default_palettes.json");
            if (File.Exists(devPath))
            {
                target = devPath;
                Console.WriteLine($"开发模式：写入 {target}");
            }
        }

        Console.WriteLine("正在抓取主流色卡（GitHub 走镜像加速）…\n");
        try
        {
            var report = PaletteFetcher.RunAsync(target, overwrite).GetAwaiter().GetResult();
            return report.Colors > 0 || report.Failed.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"抓取失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 消息路由：{ id, action, payload } -&gt; { ok, reqId, data }
    ///
    /// 重要：Photino 4.x 在 JS -&gt; C# 传输中会对消息做一次转义，
    /// 使 JSON 里的双引号变成字面量 \" —— 例如
    ///     期望 {&quot;id&quot;:&quot;r1&quot;,&quot;action&quot;:&quot;getStandards&quot;}
    ///     实收 {&quot;id&quot;:&quot;r1&quot;,&quot;action&quot;:&quot;getStandards&quot;}
    /// 直接反序列化必然失败（Action 为 null，请求被静默丢弃）。
    /// 因此这里先尝试原样解析，失败则反转义后再解析。
    /// </summary>
    static async Task HandleAsync(PhotinoWindow window, string raw)
    {
        Request? req = ParseRequest(raw);
        if (req?.Action is null)
        {
            Trace.Note("BE", "--", "路由", "无法解析出 action，消息丢弃", raw);
            Log.Warn("前端", "消息缺少 action，已忽略", (raw ?? "").Truncate(300));
            return;
        }
        Trace.Note("BE", "--", "路由", $"分发动作 {req.Action} (reqId={req.Id ?? "无"})");

        // 无副作用的读操作用 Debug，避免日志被高频刷屏
        bool verbose = req.Action is not ("convertColor" or "listPalettes" or "getStandards"
                                       or "getHueBins" or "getLogs" or "getDataDir"
                                       or "getTrace" or "exportTrace");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Debug("前端", $"请求 {req.Action} (#{req.Id})");

        try
        {
            object data = req.Action switch
            {
                "getStandards" => PrintStandards.All.Select(ToDto).ToList(),
                "getHueBins" => HueBin.Bins.Select(b => new
                    { b.Key, name = b.Name, swatch = HueBin.SwatchOf(b.Key) })
                    .GroupBy(x => x.Key)
                    .Select(g => g.First())
                    .ToList(),
                "getLogs" => ReadLogs(req.Payload),
                "getTrace" => ReadTrace(),
                "exportTrace" => new { fileName = $"colormod-trace-{DateTime.Now:HHmmss}.txt", text = Trace.Export() },
                "openLogDir" => OpenLogDir(),
                "clearLogs" => new { cleared = true, note = "内存日志已清空，文件日志保留" },
                "extract" => Extract(req.Payload),
                "savePalette" => SavePalette(req.Payload),
                "deletePalette" => DeletePalette(req.Payload),
                "listPalettes" => Store.LoadAll(),
                "importFile" => ImportFile(req.Payload),
                "exportPalettes" => ExportPalettes(req.Payload),
                "fetchPalettes" => FetchPalettesAction(),
                "convertColor" => ConvertColor(req.Payload),
                "openDataDir" => OpenDataDir(),
                "getDataDir" => new { dir = Store.DataDir },
                "pickFile" => PickFile(window, req.Payload),
                "extractData" => ExtractData(req.Payload),
                "importFileData" => ImportFileData(req.Payload),
                _ => throw new NotSupportedException($"未知操作: {req.Action}")
            };

            sw.Stop();
            if (verbose)
                Log.Info("前端", $"完成 {req.Action} (#{req.Id})，耗时 {sw.ElapsedMilliseconds} ms");

            // 异步操作（如原生文件对话框）需等待完成后再回包，
            // 否则序列化 Task 对象会得到空 JSON。
            if (data is Task<object> asyncOp) data = await asyncOp;
            else if (data is Task pending) { await pending; data = new { ok = true }; }

            Send(window, new { ok = true, reqId = req.Id, data });
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Error("前端", $"处理 {req.Action} (#{req.Id}) 失败，耗时 {sw.ElapsedMilliseconds} ms",
                      $"{ex.GetType().Name}: {ex.Message}");
            Log.Debug("前端", $"失败请求详情", $"payload: {JsonSerializer.Serialize(req.Payload, Json)?.Truncate(500)}");
            Send(window, new { ok = false, reqId = req.Id, error = ex.Message });
        }
    }

    /// <summary>
    /// 原生文件选择（Photino 4.x 提供 ShowOpenFileAsync）。
    /// 相比前端 &lt;input type="file"&gt;，它能拿到**完整路径**，
    /// 后端才能直接读取文件（input 方式只有文件名）。
    /// 异步版本不会阻塞消息回调，避免界面假死。
    /// </summary>
    static async Task<object> PickFile(PhotinoWindow window, Payload? p)
    {
        string title = p?.Title ?? "选择文件";
        bool card = p?.Kind == "card";
        var filters = card
            ? new[]
            {
                ("色卡与预设", new[] { "*.json", "*.ase", "*.aseu", "*.gpl", "*.css", "*.hex", "*.txt" }),
                ("所有文件", new[] { "*.*" }),
            }
            : new[]
            {
                ("图片", new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.webp" }),
                ("所有文件", new[] { "*.*" }),
            };

        try
        {
            var files = await window.ShowOpenFileAsync(title, null, false, filters);
            string? path = files != null && files.Length > 0 ? files[0] : null;
            Log.Info("文件", path != null ? $"已选择: {path}" : "用户取消选择");
            return new { path = path ?? string.Empty, ok = path != null };
        }
        catch (Exception ex)
        {
            Log.Exception("文件", "打开文件选择器失败", ex);
            return new { path = string.Empty, ok = false, error = ex.Message };
        }
    }

    /// <summary>把 base64 字符串解码为字节。</summary>
    static byte[] FromBase64(Payload p)
    {
        string data = p.DataBase64 ?? "";
        // 前端已去掉 data URL 前缀，这里兼容残留写法
        int comma = data.IndexOf(',');
        if (data.StartsWith("data:") && comma > 0) data = data[(comma + 1)..];
        try { return Convert.FromBase64String(data); }
        catch (Exception ex)
        {
            throw new FormatException("文件内容解码失败（base64 格式错误）: " + ex.Message);
        }
    }

    /// <summary>
    /// 取色：直接接收文件内容（base64）。
    /// 用于拖放场景 —— WebView2 不提供 File.path，拿不到磁盘路径。
    /// </summary>
    static object ExtractData(Payload? p)
    {
        if (p == null) throw new ArgumentException("缺少请求参数");
        byte[] bytes = FromBase64(p);
        string name = p.FileName ?? "拖入图片";

        // 落到临时目录复用既有解码流程
        string ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        string tmp = Path.Combine(Path.GetTempPath(),
            $"colormod_upload_{Guid.NewGuid():N}{ext}");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var r = Extractor.Extract(tmp, Math.Clamp(p.MaxColors <= 0 ? 16 : p.MaxColors, 1, 64),
                                     p.StandardId ?? "pso-coated-v3");
            Log.Info("取色", $"{name}（{bytes.Length} 字节）→ {r.Colors.Count} 色，空间 {r.SourceColorSpace}");

            return new
            {
                r.Colors, r.SourceColorSpace, r.IsCmykSource, r.IccProfileName,
                r.ImageWidth, r.ImageHeight, r.TotalPixelsAnalyzed,
                r.TotalInkCoverage, r.StandardId, r.Warnings,
                sourceFile = name,
            };
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 清理失败无碍 */ }
        }
    }

    /// <summary>导入色卡：直接接收文件内容（base64），用于拖放二进制色卡。</summary>
    static object ImportFileData(Payload? p)
    {
        if (p == null) throw new ArgumentException("缺少请求参数");
        byte[] bytes = FromBase64(p);
        string name = p.FileName ?? "拖入色卡";
        string ext = Path.GetExtension(name).ToLowerInvariant();

        Palette pal;
        if (ext is ".ase" or ".aseu")
        {
            pal = Store.ImportAse(bytes, Path.GetFileNameWithoutExtension(name));
        }
        else
        {
            // 文本类：按 UTF-8 解码后复用文本导入
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            pal = ext switch
            {
                ".gpl" => Store.ImportGplText(text, Path.GetFileNameWithoutExtension(name)),
                ".css" => Store.ImportCssText(text, Path.GetFileNameWithoutExtension(name)),
                _ => Store.ImportHexListText(text, Path.GetFileNameWithoutExtension(name)),
            };
        }

        if (pal.Colors.Count == 0)
            throw new FormatException($"未能从 {name} 解析出颜色");

        Store.Add(pal);
        Log.Info("导入", $"{name}（内容上传）→ {pal.Colors.Count} 色");
        return new { saved = new[] { pal }, count = pal.Colors.Count, paletteCount = 1 };
    }

    /// <summary>读取消息追踪，供界面展示与导出。</summary>
    static object ReadTrace()
    {
        var list = Trace.Read(600);
        Log.Info("日志", $"读取追踪 {list.Count} 条");
        return new
        {
            entries = list.Select(e => new
            {
                seq = e.Seq,
                time = e.Time,
                side = e.Side,
                dir = e.Dir,
                channel = e.Channel,
                summary = e.Summary,
                bytes = e.Bytes,
                hash = e.Hash,
                detail = e.Detail,
            }),
            file = Trace.CurrentFile,
            dir = Log.LogDir,
            total = list.Count,
        };
    }

    /// <summary>读取系统日志供界面展示。</summary>
    static object ReadLogs(Payload? p)
    {
        int max = Math.Clamp(p?.MaxColors > 0 ? p.MaxColors : 300, 1, 1000);
        var min = (p?.MinLevel ?? "Debug") switch
        {
            "Info" => LogLevel.Info,
            "Warn" => LogLevel.Warn,
            "Error" => LogLevel.Error,
            _ => LogLevel.Debug,
        };
        var list = Log.Read(max, min);
        Log.Info("日志", $"读取日志 {list.Count} 条（级别 ≥ {min}）");
        return new
        {
            entries = list.Select(e => new
            {
                time = e.Time.ToString("HH:mm:ss.fff"),
                level = e.Level.ToString(),
                category = e.Category,
                message = e.Message,
                detail = e.Detail,
            }),
            dir = Log.LogDir,
            file = System.IO.Path.Combine(Log.LogDir, $"colormod-{DateTime.Now:yyyy-MM-dd}.log"),
        };
    }

    /// <summary>打开日志目录（必要时先写一条以确保目录存在）。</summary>
    static object OpenLogDir()
    {
        try
        {
            Log.Info("日志", "用户请求打开日志目录");
            Directory.CreateDirectory(Log.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.LogDir,
                UseShellExecute = true,
            });
            return new { opened = true, dir = Log.LogDir };
        }
        catch (Exception ex)
        {
            Log.Exception("日志", "打开日志目录失败", ex);
            return new { opened = false, dir = Log.LogDir, error = ex.Message };
        }
    }

    // ---------- 具体操作 ----------

    /// <summary>在数据目录打开文件夹（Windows 资源管理器）。</summary>
    static object OpenDataDir()
    {
        try
        {
            string dir = Store.DataDir;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
            return new { opened = true, dir };
        }
        catch (Exception ex)
        {
            return new { opened = false, dir = Store.DataDir, error = ex.Message };
        }
    }

    /// <summary>GUI 内触发色卡抓取，写回内置默认预设文件。</summary>
    static object FetchPalettesAction()
    {
        string target = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "default_palettes.json");
        var report = PaletteFetcher.RunAsync(target, overwrite: true).GetAwaiter().GetResult();
        return new
        {
            report.Palettes,
            report.Colors,
            report.Ok,
            report.Failed,
            target,
        };
    }


    static object Extract(Payload? p)
    {
        string? path = p?.Path;
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("未提供图片路径");
        if (!File.Exists(path)) throw new FileNotFoundException("图片不存在: " + path);
        int maxColors = Math.Clamp(p?.MaxColors ?? 16, 1, 64);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Extractor.Extract(path!, maxColors, p?.StandardId ?? "pso-coated-v3");
        sw.Stop();
        Log.Info("取色", $"{Path.GetFileName(path)}：{r.ImageWidth}×{r.ImageHeight}，" +
                       $"空间={r.SourceColorSpace}，提取 {r.Colors.Count} 色，" +
                       $"平均总墨量 {r.TotalInkCoverage:F1}%，耗时 {sw.ElapsedMilliseconds} ms",
            string.Join(" | ", r.Warnings));
        return new
        {
            r.Colors, r.SourceColorSpace, r.IsCmykSource, r.IccProfileName,
            r.ImageWidth, r.ImageHeight, r.TotalPixelsAnalyzed,
            r.TotalInkCoverage, r.StandardId, r.Warnings,
            sourceFile = Path.GetFileName(path),
        };
    }

    static object SavePalette(Payload? p)
    {
        var colors = (p?.Colors ?? new()).Select(c => new PaletteColor
        {
            Name = c.Name ?? "",
            Hex = (c.Hex ?? "#000000").ToLowerInvariant(),
            HueBin = c.HueBin ?? "gray",
            Family = c.Family ?? "",
        }).ToList();

        var pal = new Palette
        {
            Name = string.IsNullOrWhiteSpace(p?.Name) ? "未命名预设" : p!.Name!.Trim(),
            Source = p?.Source ?? "custom",
            StandardId = p?.StandardId ?? "pso-coated-v3",
            Colors = colors,
        };
        Store.Add(pal);
        Log.Info("预设", $"已保存预设「{pal.Name}」，{pal.Colors.Count} 色，标准 {pal.StandardId}");
        return pal;
    }

    static object DeletePalette(Payload? p)
    {
        if (string.IsNullOrWhiteSpace(p?.Id)) throw new ArgumentException("未提供预设 ID");
        bool ok = Store.Delete(p!.Id!);
        Log.Info("预设", ok ? $"已删除预设 {p.Id}" : $"未找到要删除的预设 {p.Id}");
        return new { deleted = ok };
    }

    static object ImportFile(Payload? p)
    {
        var path = p?.Path;
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("未提供文件路径");
        if (!File.Exists(path)) throw new FileNotFoundException("文件不存在: " + path);

        var ext = Path.GetExtension(path).ToLowerInvariant();

        // ASE 为二进制，单独处理
        if (ext is ".ase" or ".aseu")
        {
            var pal = Store.ImportAse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
            if (pal.Colors.Count == 0)
                throw new FormatException("ASE 文件中未找到颜色");
            Store.Add(pal);
            Log.Info("导入", $"Adobe ASE：{Path.GetFileName(path)} -> {pal.Colors.Count} 色");
            return new
            {
                saved = new[] { pal },
                count = pal.Colors.Count,
                paletteCount = 1,
                format = "Adobe ASE 色卡",
                path,
            };
        }

        var (palettes, fmt) = Store.ImportFileAll(path);
        if (palettes.Count == 0 || palettes.Sum(x => x.Colors.Count) == 0)
            throw new FormatException("该文件中未找到颜色");

        // 多预设逐个入库，各自保留原名
        foreach (var pal in palettes) Store.Add(pal);
        Log.Info("导入", $"{Path.GetFileName(path)}（{fmt}）：{palettes.Count} 个预设 / " +
                         $"{palettes.Sum(x => x.Colors.Count)} 色");

        return new
        {
            saved = palettes,
            count = palettes.Sum(x => x.Colors.Count),
            paletteCount = palettes.Count,
            format = fmt,
            path,
        };
    }

    static object ExportPalettes(Payload? p)
    {
        var ids = p?.Ids ?? new List<string>();
        var all = Store.LoadAll();
        var list = ids.Count > 0 ? all.Where(x => ids.Contains(x.Id)).ToList() : all;
        Log.Info("导出", ids.Count > 0
            ? $"导出指定 {list.Count} 个预设"
            : $"导出全部 {list.Count} 个预设");
        return new
        {
            fileName = $"colormod-palettes-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            json = Store.ExportJson(list),
            count = list.Count,
        };
    }

    /// <summary>单色换算：HEX -&gt; CMYK/RGB/Lab，按指定印刷标准，带 TAC 校验。</summary>
    static object ConvertColor(Payload? p)
    {
        var rgb = ColorMath.FromHex(p?.Hex ?? "#000000");
        var std = PrintStandards.ById(p?.StandardId);
        var cmyk = std.ToCmyk(rgb);
        var lab = ColorMath.RgbToLab(rgb);
        var hsl = ColorMath.RgbToHsl(rgb);
        var issues = std.Validate(cmyk);
        var white = new ColorMath.Rgb(255, 255, 255);
        return new
        {
            hex = ColorMath.ToHex(rgb),
            rgb = new { rgb.R, rgb.G, rgb.B },
            cmyk = new { cmyk.C, cmyk.M, cmyk.Y, cmyk.K, totalInk = PrintStandard.TotalInk(cmyk) },
            lab = new { lab.L, lab.A, lab.B },
            hsl = new { hsl.H, hsl.S, hsl.L },
            standard = std.Id,
            tacLimit = std.TacLimit,
            tacExceeded = std.IsTacExceeded(cmyk),
            issues,
            contrastOnWhite = Math.Round(ColorMath.ContrastRatio(rgb, white), 2),
            suggestBlackText = !ColorMath.IsDark(rgb),
        };
    }



    // ---------- DTO / 工具 ----------

    static object ToDto(PrintStandard s) => new
    {
        s.Id, s.Name, s.PaperType, s.Region, s.Process,
        tacLimit = s.TacLimit, s.TolerableDeltaE, s.MaxDeltaE, s.Note,
        paperWhite = new { s.PaperWhite.L, s.PaperWhite.A, s.PaperWhite.B },
        solidC = new { s.SolidCyan.L, s.SolidCyan.A, s.SolidCyan.B },
        solidM = new { s.SolidMagenta.L, s.SolidMagenta.A, s.SolidMagenta.B },
        solidY = new { s.SolidYellow.L, s.SolidYellow.A, s.SolidYellow.B },
        solidK = new { s.SolidBlack.L, s.SolidBlack.A, s.SolidBlack.B },
    };

    /// <summary>
    /// C# -&gt; JS 发送响应。
    /// 关键点：不能在此处（WebMessageReceived 回调内）同步发送。
    /// 回调正运行在 WebView 的消息处理上下文中，此时再调 SendWebMessage
    /// 会与之重入，导致消息被静默丢弃 —— 表现为前端一直等到超时。
    /// 证据：同一窗口里 Timer 线程发的心跳能收到，回调内发的响应收不到。
    /// 因此改为投递到后台队列，由独立线程发出。
    /// </summary>
    static void Send(PhotinoWindow window, object data)
    {
        string json = JsonSerializer.Serialize(data, Json);
        if (!_windowReady)
            Trace.Note("BE", "OUT", "入队", $"窗口未就绪，响应仍入队并等待 ({json.Length} 字符)", json);
        else
            Trace.Note("BE", "OUT", "入队", $"响应入队 ({json.Length} 字符)", json);
        Outbound.Enqueue((window, json));
    }

    /// <summary>待发送队列：解决回调内发送的重入问题。</summary>
    static readonly System.Collections.Concurrent.ConcurrentQueue<(PhotinoWindow, string)> Outbound = new();

    /// <summary>
    /// 窗口是否已就绪。WebView 尚未初始化完成时 SendWebMessage 会静默失败，
    /// 导致响应入队了却发不出去（前端一直等到超时）。
    /// </summary>
    static volatile bool _windowReady;

    /// <summary>页面就绪后由前端心跳触发。</summary>
    static void MarkWindowReady()
    {
        if (!_windowReady)
        {
            _windowReady = true;
            Log.Info("窗口", "已收到前端心跳，标记窗口就绪");
        }
    }

    /// <summary>启动发送线程（在窗口创建后调用）。</summary>
    static void StartSender()
    {
        var th = new Thread(() =>
        {
            while (true)
            {
                // 窗口未就绪时暂缓取出，等前端心跳到达后再发，
                // 否则此时 SendWebMessage 会静默失败导致消息丢失。
                if (!_windowReady)
                {
                    Thread.Sleep(20);
                    continue;
                }
                if (Outbound.TryDequeue(out var item))
                {
                    try
                    {
                        (var win, var msg) = item;
                        Trace.Note("BE", "OUT", "SendWebMessage",
                            $"调用原生发送 ({msg.Length} 字符)", msg);
                        win.SendWebMessage(msg);
                        Trace.Note("BE", "OUT", "SendWebMessage", "原生发送调用已返回（未抛异常）");
                    }
                    catch (Exception ex)
                    {
                        Trace.Note("BE", "OUT", "SendWebMessage", "原生发送抛出异常: " + ex.Message);
                        Log.Exception("通信", "发送响应失败", ex);
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        })
        { IsBackground = true, Name = "ColorMod-Sender" };
        th.Start();
        Log.Info("通信", "发送线程已启动");
    }

    /// <summary>
    /// 解析前端请求，兼容被 Photino 转义过的消息。
    /// 先原样解析；失败则把 \" 与 \\ 还原后重试。
    /// </summary>
    static Request? ParseRequest(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 1) 原样解析（未经转义的正常路径）
        try
        {
            var ok = JsonSerializer.Deserialize<Request>(raw, Json);
            if (ok?.Action is not null) return ok;
        }
        catch { /* 走反转义分支 */ }

        // 2) 反转义后重试
        try
        {
            string fixedJson = Unescape(raw!);
            var again = JsonSerializer.Deserialize<Request>(fixedJson, Json);
            if (again?.Action is not null)
            {
                Trace.Note("BE", "IN", "解析", "原样解析失败，转义还原后成功", fixedJson);
                return again;
            }
            Trace.Note("BE", "IN", "解析", "还原后仍无 action", fixedJson);
        }
        catch (Exception ex)
        {
            Trace.Note("BE", "IN", "解析", "还原后仍失败: " + ex.Message, raw);
        }
        return null;
    }

    /// <summary>
    /// 还原被过度转义的 JSON 文本。
    /// 把 \" 还原为 "，\\ 还原为 \，\\&quot; 等常见组合一并处理。
    /// 仅在原样解析失败时调用，不影响正常路径。
    /// </summary>
    static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char next = s[i + 1];
                if (next is '"' or '\\' or '/' or 'n' or 't' or 'r' or 'b' or 'f'
                    or 'u' or 'a' or 'c' or 'd' or 'e' or 'l' or 's' or 'p')
                {
                    // \u 形式保留（可能是 \u4e2d），其余按 JSON 转义还原
                    if (next == 'u')
                    {
                        sb.Append(s, i, Math.Min(6, s.Length - i));
                        i += 5;
                        continue;
                    }
                    sb.Append(next switch
                    {
                        'n' => '\n', 't' => '\t', 'r' => '\r',
                        'b' => '\b', 'f' => '\f',
                        _ => next,
                    });
                    i++;
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    sealed class Request
    {
        public string? Id { get; set; }
        public string? Action { get; set; }
        public Payload? Payload { get; set; }
    }

    sealed class Payload
    {
        public string? Path { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Hex { get; set; }
        public string? Source { get; set; }
        public string? StandardId { get; set; }
        public int MaxColors { get; set; }
        public string? MinLevel { get; set; }
        public string? Title { get; set; }
        public string? Kind { get; set; }   // image / card
        public string? FileName { get; set; }
        public string? DataBase64 { get; set; }
        public bool IsBase64 { get; set; }
        public List<string>? Ids { get; set; }
        public List<ColorInput>? Colors { get; set; }
    }

    sealed class ColorInput
    {
        public string? Name { get; set; }
        public string? Hex { get; set; }
        public string? HueBin { get; set; }
        public string? Family { get; set; }
    }
}
