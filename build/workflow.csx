#!/usr/bin/env dotnet-script
// =============================================================================
// ColorMod GitHub Actions 工作流生成器
//
// 仓库内只允许 csx / ps1 / py 三种脚本，故 CI 配置不作为 .yml 存放在仓库，
// 而由本脚本按需生成到 .github/workflows/release.yml。
//
// 用法：
//   dotnet-script build/workflow.csx            # 生成工作流
//   dotnet-script build/workflow.csx -- --check # 只检查是否存在，不写入
//   dotnet-script build/workflow.csx -- --force # 覆盖已有文件
// =============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text;

try { Console.OutputEncoding = Encoding.UTF8; } catch { }

string[] rawArgs = ReadScriptArgs();

static string[] ReadScriptArgs()
{
    var all = Environment.GetCommandLineArgs();
    int sep = Array.IndexOf(all, "--");
    if (sep >= 0 && sep + 1 < all.Length) return all.Skip(sep + 1).ToArray();
    return all.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray();
}

bool hasFlag(string n) => rawArgs.Any(a => a.Equals("--" + n, StringComparison.OrdinalIgnoreCase));

void Say(string level, string msg)
{
    var t = DateTime.Now.ToString("HH:mm:ss");
    Console.WriteLine($"[{t}] {level,-4} │ {msg}");
}

var root = FindRepoRoot();
string outDir = Path.Combine(root, ".github", "workflows");
string outFile = Path.Combine(outDir, "release.yml");

Say("INFO", $"仓库根目录：{root}");
Say("INFO", $"目标文件：  {outFile}");

if (hasFlag("check"))
{
    bool exists = File.Exists(outFile);
    Say(exists ? " OK " : "WARN", exists
        ? "工作流已存在（如需重新生成请加 --force）"
        : "工作流不存在，执行不带 --check 的命令即可生成");
    return exists ? 0 : 1;
}

bool exists = File.Exists(outFile);
if (exists && !hasFlag("force"))
{
    Say(" OK ", "工作流已存在，跳过（如需覆盖请加 --force）");
    return 0;
}

Directory.CreateDirectory(outDir);

// ---------------------------------------------------------------- 工作流内容
// 说明：YAML 中的 ${{ }} 是 GitHub Actions 表达式，
// 用普通字符串逐行拼装，避免 C# 字符串插值把花括号误解析。
var lines = new StringBuilder();
void L(string s = "") => lines.AppendLine(s);

L("# 由 build/workflow.csx 生成 —— 请勿手工编辑，改脚本后重新生成");
L("name: 跨平台构建与发布");
L();
L("on:");
L("  push:");
L("    branches: [main, master]");
L("    tags: ['v*']");
L("  pull_request:");
L("    branches: [main, master]");
L("  workflow_dispatch:");
L("    inputs:");
L("      publish:");
L("        description: '构建后是否发布安装包（上传 Releases，仓库页面可直接下载）'");
L("        type: boolean");
L("        default: true");
L("      formats:");
L("        description: '分发格式（逗号分隔：zip,setup,deb,dmg）'");
L("        type: string");
L("        default: 'zip,setup,deb,dmg'");
L("      self_contained:");
L("        description: '生成自包含包（目标机无需预装 .NET）'");
L("        type: boolean");
L("        default: true");
L();
L("permissions:");
L("  contents: write");
L();
L("env:");
L("  DOTNET_NOLOGO: 'true'");
L("  DOTNET_CLI_TELEMETRY_OPTOUT: 'true'");
L();
L("jobs:");
L("  build:");
L("    name: 构建 ${{ matrix.rid }}");
L("    runs-on: ${{ matrix.os }}");
L("    timeout-minutes: 30");
L();
L("    strategy:");
L("      fail-fast: false");
L("      matrix:");
L("        include:");
L("          - rid: win-x64");
L("            os: windows-latest");
L("          - rid: win-arm64");
L("            os: windows-latest");
L("          - rid: linux-x64");
L("            os: ubuntu-latest");
L("          - rid: linux-arm64");
L("            os: ubuntu-24.04-arm");
L("          - rid: osx-x64");
L("            os: macos-13");
L("          - rid: osx-arm64");
L("            os: macos-latest");
L();
L("    steps:");
L("      - name: 检出代码");
L("        uses: actions/checkout@v4");
L();
L("      - name: 安装 .NET 8 SDK");
L("        uses: actions/setup-dotnet@v4");
L("        with:");
L("          dotnet-version: '8.0.x'");
L();
L("      - name: 安装打包脚本依赖");
L("        run: |");
L("          dotnet tool install -g dotnet-script");
L("          echo \"$HOME/.dotnet/tools\" >> \"$GITHUB_PATH\"");
L("        shell: bash");
L();
L("      # 构建是交叉编译，不需要 WebKit —— 它是运行时依赖，");
L("      # 仅目标 Linux 机器安装时才需要（apt install libwebkit2gtk-4.1-0）。");
L("      - name: 核心算法自测");
L("        run: |");
L("          dotnet build ColorCardStudio.csproj -c Release --nologo");
L("          ./bin/Release/net8.0/ColorMod --selftest");
L();
L("      - name: 生成安装包");
L("        shell: bash");
L("        run: |");
L("          set -euo pipefail");
L("          FORMATS='${{ inputs.formats }}'");
L("          [ -z \"$FORMATS\" ] && FORMATS='zip,setup,deb,dmg'");
L("          ARGS=\"--runtime ${{ matrix.rid }} --configuration Release\"");
L("          for f in ${FORMATS//,/ }; do");
L("            ARGS=\"$ARGS --$f\"");
L("          done");
L("          if [ '${{ inputs.self_contained }}' = 'true' ]; then");
L("            ARGS=\"$ARGS --self-contained\"");
L("          fi");
L("          echo \">> 打包参数: $ARGS\"");
L("          dotnet-script build/pack.csx -- $ARGS");
L();
L("      - name: 上传产物");
L("        uses: actions/upload-artifact@v4");
L("        with:");
L("          name: ColorMod-${{ matrix.rid }}");
L("          path: |");
L("            artifacts/${{ matrix.rid }}/**");
L("            artifacts/*.zip");
L("            artifacts/*.deb");
L("            artifacts/*.dmg");
L("            artifacts/*.exe");
L("            artifacts/PLATFORMS.md");
L("          if-no-files-found: warn");
L("          retention-days: 30");
L();
L("  release:");
L("    name: 汇总并分发安装包");
L("    needs: build");
L("    runs-on: ubuntu-latest");
L("    if: startsWith(github.ref, 'refs/tags/v') || inputs.publish != false");
L();
L("    steps:");
L("      - name: 检出代码");
L("        uses: actions/checkout@v4");
L();
L("      - name: 下载全部平台产物");
L("        uses: actions/download-artifact@v4");
L("        with:");
L("          path: dist");
L("          pattern: ColorMod-*");
L("          merge-multiple: true");
L();
L("      - name: 列出产物");
L("        run: |");
L("          find dist -maxdepth 2 -type f \\( -name '*.zip' -o -name '*.deb' \\");
L("            -o -name '*.dmg' -o -name '*Setup*.exe' \\) -printf '%p  %s bytes\\n' | sort");
L();
L("      - name: 生成安装说明");
L("        shell: bash");
L("        run: |");
L("          {");
L("            echo '## ColorMod 色卡工坊'");
L("            echo");
L("            echo 'CMYK 色卡查询与图片取色工具。下载对应平台的安装包：'");
L("            echo");
L("            echo '| 平台 | 安装包 | 使用方式 |'");
L("            echo '| --- | --- | --- |'");
L("            echo '| Windows x64 | `ColorMod-Setup-win-x64.exe` | 双击即安装，目标机无需 .NET |'");
L("            echo '| Linux x64 | `colormod_*_amd64.deb` | `sudo dpkg -i *.deb` |'");
L("            echo '| macOS Apple Silicon | `ColorMod-osx-arm64-*.dmg` | 拖入应用程序 |'");
L("            echo");
L("            echo '其余平台见下方附件（zip 为通用包，解压后直接运行）。'");
L("            echo");
L("            echo '### Linux 运行时依赖'");
L("            echo '### Linux 运行时依赖'");
L("            echo 'WebKit 是运行时依赖，安装后如无法启动请执行：'");
L("            echo");
L("            echo '```bash'");
L("            echo '# Ubuntu 24.04+ / Debian 12+'");
L("            echo 'sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0'");
L("            echo '```'");
L("          } > dist/INSTALL.md");
L();
L("      - name: 删除同名旧 Release");
L("        shell: bash");
L("        run: gh release delete \"$RELEASE_TAG\" --yes --cleanup-tag 2>/dev/null || true");
L("        env:");
L("          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}");
L("          RELEASE_TAG: ${{ github.ref_name }}");
L();
L("      - name: 发布安装包到 Releases");
L("        uses: softprops/action-gh-release@v2");
L("        with:");
L("          # 普通 push / 手动触发统一用 rolling 标签，保证仓库页面始终可下载");
L("          tag_name: ${{ startsWith(github.ref, 'refs/tags/v') && github.ref_name || 'rolling' }}");
L("          name: ColorMod 色卡工坊 ${{ startsWith(github.ref, 'refs/tags/v') && github.ref_name || 'rolling' }}");
L("          prerelease: ${{ !startsWith(github.ref, 'refs/tags/v') }}");
L("          files: |");
L("            dist/*.zip");
L("            dist/*.deb");
L("            dist/*.dmg");
L("            dist/*Setup*.exe");
L("            dist/INSTALL.md");
L("          fail_on_unmatched_files: false");
L("          generate_release_notes: true");

File.WriteAllText(outFile, lines.ToString(), new UTF8Encoding(false));

Say(" OK ", $"已生成工作流（{new FileInfo(outFile).Length} 字节）");
Say("INFO", "下一步：dotnet-script build/push.csx 推送即可触发云构建");
return 0;

// ---------------------------------------------------------------- 辅助
string FindRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ColorCardStudio.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    var scriptDir = Path.GetDirectoryName(
        Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
        ?? Environment.CurrentDirectory);
    return new DirectoryInfo(scriptDir).Parent?.FullName ?? Environment.CurrentDirectory;
}