using System.Runtime.InteropServices;

namespace ColorCardStudio;

/// <summary>
/// WinExe 默认无控制台，自测时附加到父控制台以便查看输出。
/// </summary>
static class ConsoleRunner
{
    const uint AttachParentProcess = 0xFFFFFFFF;
    const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AllocConsole();

    /// <summary>若已有可用控制台则挂接，否则新建一个。失败不影响程序运行。</summary>
    public static void Attach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess)) AllocConsole();

            var stdout = GetStdHandle(StdOutputHandle);
            if (stdout != IntPtr.Zero && stdout != new IntPtr(-1))
            {
                var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(writer);
            }
        }
        catch
        {
            // 无控制台环境（如双击运行）时静默忽略，退出码仍有效
        }
    }
}
