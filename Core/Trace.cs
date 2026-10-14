using System.Globalization;
using System.Text;

namespace ColorCardStudio.Core;

public sealed record TraceEntry(
    long Seq, string Time, string Side, string Dir, string Channel,
    string Summary, int Bytes, string Hash, string Detail);

/// <summary>
/// 端到端消息追踪。
/// 目的：把「消息在哪一环断掉」变成可观测事实，而不是靠猜。
///
/// 每个方向上的每条消息都会分配全局递增序号（#0001、#0002 …），
/// 两端（后端 / 前端）用同一套格式记录，最后按序号对齐即可看出断点：
///   - 后端有 #N 发出、前端无 #N 收到  → 传输环节丢失
///   - 前端有 #N 收到但解析失败        → 格式问题（Detail 会给出前 200 字节）
///   - 后端无 #N 记录                  → 根本没走到发送这一步
///
/// 追踪写入独立文件 colormod-trace-HHMMSS.log，与业务日志分开，
/// 避免正常日志把它淹没。
/// </summary>
public static class Trace
{
    static readonly object Gate = new();
    static readonly List<TraceEntry> Entries = new();
    static long _seq;
    static string? _file;
    static bool _enabled = true;

    /// <summary>最多保留的追踪条数（超出丢最旧的，文件仍完整）。</summary>
    const int MaxBuffered = 2000;

    /// <summary>是否启用追踪。可用 --no-trace 关闭。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set { _enabled = value; }
    }

    static string TraceFile
    {
        get
        {
            _file ??= Path.Combine(Log.LogDir,
                $"colormod-trace-{DateTime.Now:HHmmss}.log");
            return _file;
        }
    }

    /// <summary>记录一条追踪。payload 用于计算长度与哈希，summary 给人看。</summary>
    public static void Note(string side, string dir, string channel,
                            string summary, string? payload = null)
    {
        if (!_enabled) return;
        try
        {
            string body = payload ?? "";
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            var entry = new TraceEntry(
                Seq: Interlocked.Increment(ref _seq),
                Time: DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Side: side,
                Dir: dir,
                Channel: channel,
                Summary: summary,
                Bytes: bytes.Length,
                Hash: Fnv1a(bytes),
                Detail: Preview(body));

            lock (Gate)
            {
                Entries.Add(entry);
                while (Entries.Count > MaxBuffered) Entries.RemoveAt(0);
                File.AppendAllText(TraceFile, Format(entry) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 追踪失败绝不影响主流程
        }
    }

    /// <summary>
    /// 提取可读预览：前 200 字符。
    /// 注意：这里只把控制字符可见化，**不转义引号与反斜杠** ——
    /// 早期版本做了转义，导致日志里出现 \" 这种假象，
    /// 误让人以为传输层发生了转义。日志必须所见即所得。
    /// </summary>
    static string Preview(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string head = s.Length <= 200 ? s : s[..200] + "…";
        var sb = new StringBuilder(head.Length + 8);
        foreach (char c in head)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>FNV-1a 32 位哈希，用于比对两端内容是否一致。</summary>
    static string Fnv1a(byte[] data)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (byte b in data)
            {
                hash ^= b;
                hash *= 16777619;
            }
            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    static string Format(TraceEntry e) =>
        $"#{e.Seq:0000} {e.Time} [{e.Side}/{e.Dir}] {e.Channel,-22} " +
        $"{e.Bytes,7}B h={e.Hash} {e.Summary}\n         {e.Detail}";

    /// <summary>读取追踪（用于界面展示）。</summary>
    public static List<TraceEntry> Read(int max = 500)
    {
        lock (Gate)
        {
            return Entries.TakeLast(max).Reverse().ToList();
        }
    }

    /// <summary>导出追踪全文。</summary>
    public static string Export()
    {
        lock (Gate)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# ColorMod 消息追踪  共 {Entries.Count} 条  文件: {TraceFile}");
            sb.AppendLine($"# 字段: #序号 时间 [端/方向] 通道 字节数 哈希 摘要");
            sb.AppendLine();
            foreach (var e in Entries) sb.AppendLine(Format(e));
            return sb.ToString();
        }
    }

    public static string CurrentFile => TraceFile;

    /// <summary>清空内存缓冲（文件保留）。</summary>
    public static void ClearBuffer()
    {
        lock (Gate) Entries.Clear();
    }
}
