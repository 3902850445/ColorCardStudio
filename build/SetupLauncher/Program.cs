// ColorMod 自解压安装程序（模板文件）
// 逻辑：读取随 exe 嵌入的 payload.zip -> 解压到 %LOCALAPPDATA%\Programs\ColorMod -> 启动主程序
//
// 由 build/pack.csx 复制并编译；payload.zip 作为嵌入资源打进本程序。
// 单文件发布后使用者只需这一个 exe，双击即完成「安装 + 启动」。

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace ColorModSetup;

internal static class Program
{
    static int Main()
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "ColorMod");

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? payloadName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

            if (payloadName is null)
            {
                Console.Error.WriteLine("安装失败：未找到内嵌的 payload.zip");
                return 1;
            }

            Console.WriteLine("正在安装到 " + installDir + " …");

            using (var res = asm.GetManifestResourceStream(payloadName))
            using (var ms = new MemoryStream())
            {
                res!.CopyTo(ms);
                ms.Position = 0;

                Directory.CreateDirectory(installDir);
                string root = Path.GetFullPath(installDir);

                using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);
                foreach (var entry in zip.Entries)
                {
                    // 防目录穿越（Zip Slip）
                    string target = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("安装失败：压缩包含非法路径 " + entry.FullName);
                        return 2;
                    }

                    if (entry.FullName.EndsWith("/") || entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    string? parent = Path.GetDirectoryName(target);
                    if (parent != null) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(target, true);
                }
            }

            var exe = Path.Combine(installDir, "ColorMod.exe");
            if (!File.Exists(exe))
            {
                Console.Error.WriteLine("安装失败：未找到 ColorMod.exe");
                return 3;
            }

            Console.WriteLine("安装完成：" + installDir);
            Console.WriteLine("提示：卸载即删除该文件夹；预设与日志保存在 %APPDATA%\\ColorCardStudio");

            // 启动主程序（延迟一点，确保控制台信息可见）
            Thread.Sleep(600);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("安装失败：" + ex.Message);
            return 4;
        }
    }
}
