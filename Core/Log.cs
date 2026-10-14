using System.Text;

namespace ColorCardStudio.Core;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(
    DateTimeOffset Time, LogLevel Level, string Category, string Message, string? Detail);

/// <summary>
/// 系统日志。写入数据目录下的日志文件（按天分文件，滚动保留 7 天），
/// 同时保留内存环形缓冲，供界面「系统日志」面板读取。
/// 任何日志失败都不得影响主流程。
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    static readonly Queue<LogEntry> Buffer = new();
    const int MaxBuffered = 500;
    static string? _dir;

    /// <summary>日志输出目录：与预设数据同目录下的 logs/。</summary>
    public static string LogDir
    {
        get
        {
            _dir ??= Path.Combine(PaletteStore.ResolveDataDir(), "logs");
            return _dir;
        }
    }

    static string TodayFile => Path.Combine(LogDir, $"colormod-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Debug(string category, string message, string? detail = null)
        => Write(LogLevel.Debug, category, message, detail);

    public static void Info(string category, string message, string? detail = null)
        => Write(LogLevel.Info, category, message, detail);

    public static void Warn(string category, string message, string? detail = null)
        => Write(LogLevel.Warn, category, message, detail);

    public static void Error(string category, string message, string? detail = null)
        => Write(LogLevel.Error, category, message, detail);

    /// <summary>记录异常（含类型与消息），这是排查问题的第一手材料。</summary>
    public static void Exception(string category, string message, Exception ex) =>
        Write(LogLevel.Error, category, message, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    static void Write(LogLevel level, string category, string message, string? detail)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message, detail);

        lock (Gate)
        {
            Buffer.Enqueue(entry);
            while (Buffer.Count > MaxBuffered) Buffer.Dequeue();

            try
            {
                Directory.CreateDirectory(LogDir);
                var sb = new StringBuilder();
                sb.Append(entry.Time.ToString("HH:mm:ss.fff"))
                  .Append(" [").Append(LevelTag(level)).Append("] ")
                  .Append('[').Append(category).Append("] ")
                  .AppendLine(message);
                if (!string.IsNullOrWhiteSpace(detail))
                    sb.AppendLine("        " + detail!.Replace("\n", "\n        "));
                File.AppendAllText(TodayFile, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志失败静默处理，绝不影响主流程
            }
        }

        Console.WriteLine($"[{entry.Time:HH:mm:ss}] [{LevelTag(level)}] [{category}] {message}");
        if (!string.IsNullOrWhiteSpace(detail) && level >= LogLevel.Warn)
            Console.WriteLine("        " + detail);
    }

    static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };

    /// <summary>读取日志（可选按级别过滤）。</summary>
    public static List<LogEntry> Read(int max = 300, LogLevel minLevel = LogLevel.Debug)
    {
        lock (Gate)
        {
            return Buffer.Where(e => e.Level >= minLevel)
                         .TakeLast(max)
                         .Reverse()
                         .ToList();
        }
    }

    /// <summary>读取日志文件全文（用于「打开日志目录」或导出）。</summary>
    public static string ReadToday()
    {
        try
        {
            string f = TodayFile;
            return File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : "";
        }
        catch (Exception ex)
        {
            return "读取日志失败：" + ex.Message;
        }
    }

    /// <summary>清理超过保留期的旧日志文件。</summary>
    public static void Cleanup(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(LogDir, "colormod-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    try { File.Delete(f); } catch { /* 单个文件失败不影响其他 */ }
                }
            }
        }
        catch { /* 清理失败不影响主流程 */ }
    }
}

/// <summary>字符串辅助，避免日志里出现超长内容撑爆文件。</summary>
public static class StringExtensions
{
    public static string Truncate(this string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + $"…（已截断，共 {s.Length} 字符）";
    }
}
