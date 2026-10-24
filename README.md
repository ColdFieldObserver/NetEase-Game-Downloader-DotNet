# 网易游戏下载器（C# / WPF / .NET 10）

蛋仔派对（Eggy Party）官方安卓安装包，以及 MuMu 模拟器 Windows / macOS 版的官方安装包
**多线程、断点续传、完整性校验** 下载工具，使用 Vibe Coding 完成。

本仓库是原始 Python 项目的 **C# 移植版**：仅保留 **GUI（图形界面）** 形态，使用 **WPF** 作为界面框架，
运行于 **.NET 10**。原有的 CLI / TUI 命令行形态已被完全移除。

## 功能特性

- **官方直链解析**：自动从网易官方分发页 / 官方直连接口解析最新下载地址，并带有版本交叉核对。
- **多线程分段下载**：支持 1–16 线程并行下载；小文件自动收敛线程数。
- **断点续传**：下载状态落盘为 `<文件>.dlstate.json`，可随时暂停 / 取消后继续。
- **自适应并发**：开启「自适应」后，引擎按文件大小与剩余分段动态升降线程上限（最高 16）。
- **完整性校验**：下载完成后自动计算 MD5 / SHA256，APK 还会校验 **APK Signing Block v2/v3** 证书指纹，
  并与内置的网易官方证书白名单比对。
- **自我绘制速度曲线**：界面内置 Canvas 实时绘制下载速率曲线（无需任何第三方图表库）。
- **彩色日志 / 状态栏**：按级别着色的日志输出，底部状态栏实时显示当前阶段与细节。
- **单实例运行**：通过命名互斥体（Mutex）防止重复开启；再次启动时**不弹窗**，而是**把已运行的窗口置前并还原**。
- **完成提示**：下载成功后弹出提示，可一键打开所在文件夹。

## 支持的产品

| 标识 | 名称 | 平台 |
| --- | --- | --- |
| `eggy-android` | 蛋仔派对 · Android APK | Android |
| `eggy-pc` | 蛋仔派对 · PC 端游安装包 | Windows |
| `mumu-windows` | MuMu 模拟器 · Windows 版（MuMu 6） | Windows |
| `mumu-mac` | MuMu 模拟器 · macOS 版 | macOS |

产品注册表集中在 `Core/Products.cs`，新增产品只需在 `PRODUCTS` 字典中登记一条 `ProductSpec`。

## 环境要求

- **.NET 10 SDK**（`.NET 10.0` 运行时 / SDK，Windows 10 及以上）。
- Windows 桌面环境（WPF 仅支持 Windows）。

> 查看是否已安装：`dotnet --version` 应输出 `10.0.xxx`。

## 编译与运行

```bash
# 克隆或进入本目录后
dotnet build -c Release

# 运行（Release）
dotnet run -c Release
# 或直接启动生成的 exe：
# bin/Release/net10.0-windows/NeteaseGameDownloader.exe
```

> 调试：`dotnet run -c Debug` 即可在 Visual Studio / VS Code（含 C# 插件）中下断点调试。

## 界面说明

主窗口（`MainWindow.xaml`）自上而下分为：

1. **产品**：下拉选择要下载的产品（蛋仔派对 APK / PC、MuMu 模拟器 Win / Mac）。
2. **线程数**：滑块设定固定线程数（1–16），勾选「自适应」后滑块失效、由引擎动态决定上限。
3. **保存路径**：文本框显示默认保存文件名（位于程序运行目录），点击「浏览…」可另存为。
4. **选项**：
   - 下载完成后校验完整性（默认开）
   - 保留校验文件（不自动清理 `.checksum`）
   - 强制重新下载（忽略已有文件）
5. **操作按钮**：
   - **开始下载** / **查看信息**（仅探测远端文件信息，不下载）
   - **暂停** / **继续** / **取消**
   - **打开文件夹**（打开保存目录并选中已下载文件）
6. **进度区**：总进度条 + 百分比，以及已下/总计、速度、平均、剩余、已用、线程、分片等信息；
   校验阶段额外显示一条「校验进度」。
7. **速度曲线**：实时绘制下载速率曲线。
8. **日志**：按级别着色的滚动日志（INFO / OK / WARN / ERROR / DEBUG）。
9. **状态栏**：当前阶段（如「下载中」「完整性校验」「已完成」）与细节。

### 单实例行为

- 第一次启动：正常建立互斥体并进入主界面。
- 再次双击启动：检测到已有实例后，**不再弹窗提示**，而是通过事件通知旧实例把窗口
  `SetForegroundWindow` + `ShowWindow(SW_RESTORE)` 置前还原，随后退出。

## 项目结构

```
netease-game-downloader/
├── App.xaml / App.xaml.cs          # 应用入口；互斥体 + 单实例激活逻辑
├── MainWindow.xaml / .cs           # WPF 主界面与 100ms 轮询渲染
├── NeteaseGameDownloader.csproj    # WPF / net10.0-windows 工程
├── Core/
│   ├── Downloader.cs               # 多线程 Range 下载引擎（分段账本 / 尾段切分 / 看门狗 / 刷新）
│   ├── DownloadState.cs            # 断点状态 <文件>.dlstate.json 读写与重缩放
│   ├── EngineEvents.cs             # Phase 常量、DownloadProgress、ISink 事件契约
│   ├── HttpClients.cs              # 共享 HttpClient（重定向 / 不重定向 两种）
│   ├── Job.cs                      # 一次完整任务编排（解析→探测→确认→下载→校验）
│   ├── JobStatus.cs                # 线程安全状态快照，同时作为引擎 ISink
│   ├── Parser.cs                   # 分发页 JS 变量 / 兜底正则解析
│   ├── Products.cs                 # 产品注册表 + 官方版本 API + 官方直连接口
│   ├── Resolver.cs                 # 地址解析入口（手动 / 直连接口 / 分发页）
│   ├── SpeedMeter.cs               # 速率 / 进度 / 历史曲线采样
│   ├── Utils.cs                    # 日志、格式化、URL 规范化、文件辅助
│   ├── Verify.cs                   # MD5/SHA256 + APK 签名校验 + 证书白名单
│   └── WindowInterop.cs            # user32 置前/还原 P/Invoke
└── Models/
    ├── JobConfig.cs                # 任务参数（由界面表单产生）
    ├── ProductSpec.cs              # 产品元数据
    ├── Resolution.cs               # 解析结果
    └── VerifyResult.cs             # 校验结果
```

## 技术要点（与原 Python 版对照）

| 模块 | 说明 |
| --- | --- |
| 下载引擎 | 用 `Task` + `CancellationTokenSource` / `ManualResetEventSlim` / `Interlocked` 复刻 Python 的线程模型；分段账本（连续前缀 + 空洞合并）保证断点安全，队列空时做尾段二分切分。 |
| 自适应并发 | 运行初期按文件大小阶梯式拉起 worker，并随分段收敛；上限 16。 |
| 停滞看门狗 | 单个分段超过 45s 无进展即中止该 worker 的 token，触发重连 / 重试。 |
| 签名过期刷新 | 遇到 403/404（CDN 签名失效）时，自动通过 `urlRefresher` 重新解析地址并续传。 |
| 暂停 / 继续 / 取消 | 见引擎 `Pause()` / `Resume()` / `Cancel()`；取消时保存断点状态。 |
| 校验 | `Verify.cs` 单遍计算 MD5 + SHA256，并解析 APK Signing Block v2/v3 提取证书 SHA256，与内置网易官方指纹比对。 |
| 日志与渲染解耦 | 引擎只向 `ISink` 推送结构化事件；WPF 每 100ms 读取 `JobStatus.Snapshot()` 渲染，避免跨线程直接操作 UI。 |

## 与原 Python 版的关系

- 这是 **忠实移植**：下载引擎、产品注册表、解析、续传、校验、任务编排全部按原版语义用 C# 重写。
- **仅保留 GUI 形态**（WPF），CLI / TUI 已移除。
- 界面交互（产品下拉、线程、保存路径、暂停/继续/取消、进度条、速度曲线、彩色日志、状态栏、完成弹窗）对应原版 tkinter 界面。

## 免责声明

本工具仅用于下载网易**官方公开**提供的安装包，下载内容版权归网易所有。请勿将本工具用于任何侵权或违规用途。

## 许可证

本项目采用 **GPL v3** 许可证。

**禁止商业用途。** 任何个人或组织不得将本项目及其衍生作品用于商业目的。使用、修改、分发本项目时，请遵守 GPL v3 的相关条款。
