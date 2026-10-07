# ColorMod 色卡工坊

基于 **Photino.NET** 的桌面色卡查询与取色工具，以 **CMYK 为主**、兼容 RGB 与各色值代码。

## 功能

| 模块 | 说明 |
| --- | --- |
| 色卡库 | 联网整合 6 套主流色卡共 **892 色**，支持按 13 个色相分类筛选 |
| 搜索 | 支持色名 / HEX / `rgb()` / `cmyk()` 直接查询，如输入 `cmyk(0 85 38 12)` 可反查颜色 |
| 图片取色 | 读取图片提取主色，自动识别 CMYK 印刷稿与 RGB 屏幕稿；支持按钮选择与拖放 |
| 中英文色名 | 每色同时给出中文名与英文名，如「爱丽丝蓝 / aliceblue」「蓝 · 偏深 / blue-600」 |
| 点击复制 | 每个色值分量独立可点：点 C 就只复制 C，点 R 就只复制 R；整行点击复制整组 |
| HEX 整体复制 | 颜色代码作为一个整体复制，不拆分（符合代码使用习惯） |
| 详情抽屉 | 单色查看 CMYK 分色墨量条、总墨量合规、TAC 校验、Lab、HSL、WCAG 对比度 |
| 预设导入导出 | 自有 JSON 格式可**导出后原样导回**；多预设文件逐个导入并各自保留原名 |
| 第三方色卡导入 | 支持 `.ase`（Adobe）、`.gpl`（GIMP）、`.css` 变量、HEX 列表 |
| 在线更新色卡 | 界面内一键重新抓取最新主流色卡（GitHub 走镜像加速） |
| 全部色卡 | 汇总 6 套色卡 892 色，支持跨色卡搜索任意色值，卡片标注来源 |
| 最近似匹配 | 搜索到的色值不在库中时，按 Lab 距离给出最接近的颜色而非空白 |
| 印刷标准 | 内置 9 套权威标准，切换后全部 CMYK 值与 TAC 校验随之更新 |
| 系统日志 | 界面内查看系统日志，可按级别筛选、导出、打开目录 |

## 系统日志

侧栏「▤ 系统日志」。记录启动、取色、导入导出、通信与异常，按级别区分，
错误数会在侧栏显示红色徽标。

日志同时写入数据目录，按天分文件、保留 7 天：

- Windows：`%APPDATA%\ColorCardStudio\logs\colormod-YYYY-MM-DD.log`
- macOS / Linux：`~/.config/colormod/logs/colormod-YYYY-MM-DD.log`

格式为 `[时间] [级别] [分类] 消息`，异常会附类型、消息与调用栈。
点「打开目录」可直接查看，「导出」可另存为文本。

## 复制行为说明

- **色值分量**：CMYK 的 C/M/Y/K、RGB 的 R/G/B 共 7 个数字各自独立可点，
  点哪个只复制哪个（数字前有对应通道色点，悬停高亮）
- **整行点击**：复制该通道的完整值，如 `95 56 0 1` 或 `13, 110, 253`
- **HEX**：颜色代码整体复制，不拆分
- **详情抽屉**：分色墨量条的数值同样可单独复制

## 消息通信约定（踩坑记录）

前后端通过 Photino 的消息通道通信，以下四条都是实测踩出来的：

1. **JS 接收 C# 消息**必须挂在 `window.external.receiveMessage(msg)` 上，
   这是 Photino 4.x 的固定落点（4.x 没有 `InvokeScript`）。
2. **C# 的 `JsonSerializerOptions` 必须设 `PropertyNameCaseInsensitive = true`**。
   前端发 camelCase（`id`/`action`/`payload`），C# 属性是 PascalCase。
   缺失该项时**反序列化不报错，所有字段静默为 null** ——
   表现为「请求收到了但没有 action」，日志里看不出任何异常，最难排查的一类。
3. **C# 不要在 `WebMessageReceived` 回调内同步发送响应**，容易与消息循环重入。
   本项目把响应投递到 `Outbound` 队列，由独立发送线程发出。
4. **Photino 可能重复投递同一条消息**（实测同一请求收到 12 次）。
   前端按 `reqId` 去重、`pending.delete` 后忽略后续响应，因此是幂等的。

### 启动时序

WebView 在窗口创建后需要一点时间才能真正接收 `SendWebMessage`。
过早发送会**静默失败**（不抛异常、不写日志），表现为「响应入队了但前端收不到」。
本项目用一个 `_windowReady` 标志解决：收到前端首个心跳后才开始发送队列中的响应，
并在前端加了心跳自愈 —— 若第 3 次心跳时仍有请求悬空，会自动重新拉取初始数据。

### 用消息追踪定位问题

侧栏「▤ 系统日志」→ 点「消息追踪」可展开端到端追踪面板，
每一环（前端发出 → 原生接收 → 路由分发 → 响应入队 → 原生发送 → 前端收到）
都有独立记录，含序号、字节数与内容预览，两端按序号对齐即可看出断在哪一环。

追踪同时写入 `%APPDATA%\ColorCardStudio\logs\colormod-trace-HHMMSS.log`。

## 排查「界面空白 / 点击无反应」

本版本起，这类问题的成因都会被记进日志。最常见两种：

1. **不在桌面应用环境中运行** —— 直接用浏览器打开 `index.html` 时没有 Photino
   后端，取色与预设功能不可用（色卡浏览与搜索仍正常）。日志中会出现
   `未连接到桌面后端`。请通过 `ColorMod.exe` 启动。
2. **后端请求失败** —— 日志里会记录 `请求 xxx 失败` 及其原因。

界面初始化采用「先绑事件、再逐项加载」结构：任何一步失败都不会导致整体
无响应，失败项会在日志面板标红并弹出提示。

## 预设导入导出

导出为 JSON，可在本机或其他电脑原样导回：

- **导出全部预设** — 导出「我的预设」下的所有预设
- **导出当前色组** — 只导出当前正在浏览的这一组颜色
- 单个预设可在其卡片上单独导出

导入支持 ColorMod 预设 JSON（`导出 → 导入` 完整回环）、Adobe ASE、GIMP GPL、
CSS 变量、HEX/名称列表。导入时：

- 多预设文件会**逐个入库**，各自保留原名与印刷标准，不会被合并成一个
- 自动补全缺失的色相分类，非法色值跳过而非中断
- 非 ColorMod 格式会附加格式后缀以便区分

数据文件为纯 JSON，可直接用外部工具编辑：

```json
[
  {
    "name": "品牌主色",
    "standardId": "pso-coated-v3",
    "colors": [
      { "name": "主蓝", "hex": "#0d6efd", "hueBin": "sky" },
      { "name": "辅助红", "hex": "#dc2626", "hueBin": "red" }
    ]
  }
]
```

## 内置色卡来源

| 色卡 | 颜色数 | 来源 |
| --- | --- | --- |
| Radix Colors | 287 | unpkg（24 个色系） |
| Tailwind CSS v4 | 241 | unpkg（OKLCH 已在抓取期转换为 sRGB） |
| CSS 命名颜色 | 139 | color-name |
| Open Color | 132 | unpkg |
| Material Design 3 | 74 | material-web 参考调色板 |
| Bootstrap 5 | 19 | twbs scss 变量 |

联网更新（在程序目录或项目目录执行）：

```bash
ColorMod.exe --fetch-palettes            # 已有文件则跳过
ColorMod.exe --fetch-palettes --overwrite # 强制重新抓取
```

界面内点「↻ 更新色卡」等价于上述命令。单个源失败不影响其他源，
失败项会在输出中列出。

## 怎么选择印刷标准

CMYK 没有绝对值。同一个 `C0 M80 Y60 K0` 印在铜版纸和新闻纸上完全不同，
因为纸张吸墨能力与印刷工艺决定了能承载多少墨、能达到什么色域。
**标准 = 纸张类型 × 印刷工艺 × 地区惯例**，三者共同确定。

| 步骤 | 判断依据 | 对应标准 |
| --- | --- | --- |
| 1. 印什么纸 | 铜版纸 / 涂布纸 | PSO Coated v3、ISO Coated v2 |
| | 非涂布纸 / 胶版纸 | PSO Uncoated、FOGRA39 |
| 2. 什么工艺 | 单张纸胶印 | 限墨可到 330% |
| | 轮转胶印 / 商业轮转 | 限墨 260~300% |
| | 数码印刷 | CGATS TR 001 |
| 3. 地区惯例 | 欧洲 → ECI | ISO Coated / PSO 系列 |
| | 北美 → IDEAlliance | GRACoL、SWOP |
| | 日本 | Japan Color |

**权威做法**：直接向印刷厂索取 PDF/X-4 与其 ICC 配置，以厂方为准。
不确定时用 PSO Coated v3（现行国际默认，限墨 300%）。

界面「印刷标准」页顶部有同样的速查说明。

## 印刷标准

CMYK 数值脱离印刷条件没有意义，因此所有换算都绑定标准：

- ISO Coated v2 (ECI) — 330% / 300% 两个限墨变体
- PSO Coated v3 (FOGRA51)、PSO Uncoated (FOGRA47)
- GRACoL 2013、SWOP 2006、Japan Color 2011、FOGRA39、CGATS TR 001

每个标准记录纸张白点、四色实地目标 Lab、总墨量上限（TAC）与 ΔE 容差。
超出 TAC 会明确标红并给出合规提示。

> 内置标准数据依据 ISO 12647-2、FOGRA、ECI、IDEAlliance 的公开资料整理，
> 用于总墨量校验与实地色参考；**未内置 ICC 描述文件**，CMYK 为按标准特性的
> 模型换算值，不等同于基于真实 ICC 的软打样。需要严格色控时请用印刷厂提供的
> ICC 配置做软打样。

## 跨平台打包

项目为**纯 C#**（Photino.NET + 原生前端资源），打包脚本为 C# 脚本 `build/pack.csx`。

```bash
# 当前平台
dotnet-script build/pack.csx

# 全部 6 个平台（win/linux/macOS × x64/arm64）
dotnet-script build/pack.csx -- --all

# 指定平台（支持逗号分隔或多次传参）
dotnet-script build/pack.csx -- --runtime win-x64,linux-x64,osx-arm64

# 每个平台独立运行，目标机器无需预装 .NET
dotnet-script build/pack.csx -- --all --self-contained

# 打包后生成 zip，并附带平台运行时说明
dotnet-script build/pack.csx -- --all --zip
```

产物输出到 `artifacts/<rid>/`，脚本会校验主程序与 `wwwroot` 资源是否齐全。

> `dotnet-script` 可用 `dotnet tool install -g dotnet-script` 安装。

### 平台运行时要求

| 平台 | 预装 .NET | WebView 依赖 |
| --- | --- | --- |
| Windows x64/ARM64 | 需 .NET 8（或用自包含包） | Edge WebView2 Runtime（Win11 已内置） |
| Linux x64/ARM64 | 同上 | `libwebkit2gtk-4.0-37`、`libgtk-3-0` |
| macOS Intel/Apple Silicon | 同上 | 系统自带 WebKit |

Debian/Ubuntu：
```bash
sudo apt install libwebkit2gtk-4.0-37 libgtk-3-0
```

## 构建（直接用 dotnet）

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o publish
./publish/ColorMod.exe
```

依赖：.NET 8 SDK；Windows 需 WebView2 运行时（Win11/10 已内置）。

## 命令行

```bash
ColorMod.exe                      # 启动图形界面
ColorMod.exe --selftest            # 运行 74 项核心算法自测
ColorMod.exe --fetch-palettes     # 联网抓取最新色卡（已存在则跳过）
ColorMod.exe --fetch-palettes --overwrite
```

## 自测

```bash
./publish/ColorMod.exe --selftest
```

104 项检查，分组如下：

| 分组 | 覆盖内容 |
| --- | --- |
| HEX / Lab / HSL | 色彩空间双向换算与往返精度 |
| CMYK | 印刷语义、9 套标准的 TAC 限制 |
| CIEDE2000 | 对照 Sharma 论文测试数据逐对验证 |
| WCAG | 对比度与可读性判定 |
| 取色引擎 | 解码、色彩空间标注、覆盖率、排序 |
| 预设 | 持久化，ASE/GPL/CSS/JSON 导入，导出→导入回环 |
| 色相分类 | 13 个色箱的边界与降级 |
| 系统日志 | 级别、落盘、筛选、截断 |
| 前端资源 | DOM 元素齐全性、绑定顺序、容错结构、空值保护 |

前端一组专门用于捕获「界面空白 / 点击无反应」类回归：校验 JS 依赖的
DOM 元素是否都存在、事件绑定是否早于数据加载、渲染函数是否有空值保护。

## 数据目录

- Windows：`%APPDATA%\ColorCardStudio\palettes.json`
- macOS / Linux：`~/.config/colormod/palettes.json`

配置损坏时会自动备份为 `palettes.json.broken.<时间戳>`，不覆盖原文件。
界面左下角可直接打开该目录。

## 项目结构

```
Program.cs               Photino 窗口与前后端消息路由
ConsoleRunner.cs         WinExe 控制台挂接（自测/抓取用）
SelfTest.cs              74 项核心算法自测
Core/ColorMath.cs        色彩空间转换 + CIEDE2000 + WCAG
Core/PrintStandards.cs   9 套印刷标准知识库
Core/HueBin.cs           13 类色相分类（前后端唯一定义源）
Core/ColorExtractor.cs   图片解码、色彩空间探测、颜色量化
Core/PaletteStore.cs     预设持久化 + ASE/GPL/CSS/JSON 导入
Core/PaletteFetcher.cs   色卡联网抓取（GitHub 镜像加速）
Core/Log.cs              系统日志（文件 + 内存缓冲，保留 7 天）
wwwroot/                 前端界面（原生 HTML/CSS/JS）
```
