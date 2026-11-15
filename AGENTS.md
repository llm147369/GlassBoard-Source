# GlassBoard-Source — 交接说明（给任何 AI 助手 / 协作者）

## 项目

Windows 桌面启动台：毛玻璃 Dock 栏、应用启动台、控制中心、桌面组件（时钟/天气/音乐/律动）、灵动岛、壁纸引擎（静态 + 自定义视频动态壁纸）。.NET Framework 4.8，WPF + WinForms，C# LangVersion 14。

- 仓库：https://github.com/llm147369/GlassBoard-Source（remote 已配好）
- 当前版本：**v0.9.21.13**（2026-11-15）
- 工作目录：本仓库根目录（便携版运行目录与源码仓库是同一个目录，`GlassBoard.exe` 在根目录直接运行）

## 常用命令链

### 构建

```powershell
Remove-Item Env:\DOTNET_STARTUP_HOOKS -ErrorAction SilentlyContinue   # 必须！否则 dotnet CLI 崩溃
dotnet build src\GlassBoard.csproj -c Release
```

输出：`src\bin\Release\net48\GlassBoard.exe`

### 本机换装（构建后让本机跑上新 exe）

```powershell
Get-Process GlassBoard -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$env:LOCALAPPDATA\GlassBoard\watchdog.alive" -ErrorAction SilentlyContinue   # 防"异常退出"误报横幅
Copy-Item src\bin\Release\net48\GlassBoard.exe GlassBoard.exe -Force
Start-Process .\GlassBoard.exe
```

### 打包发布包

```powershell
.\pack-release.ps1          # 版本号自动读 src\Properties\AssemblyInfo.cs，可 -Version x.y.z 覆盖
```

输出：`publish\GlassBoard_v{版本}_User.zip`（必须 30 个文件，脚本会校验关键文件）
注意：pack-release.ps1 必须保持 **UTF-8 with BOM**（PowerShell 5.1 无 BOM 读中文会乱码/解析错）。

### 发版本（更新器按 Release 时间戳判"次更新"，同 tag 可重复覆盖）

```powershell
# 1) bump 版本：src\Properties\AssemblyInfo.cs 的 AssemblyVersion + AssemblyFileVersion
# 2) README.md 顶部标题版本 + 插入新"### vX.Y.Z 更新内容"段
# 3) 构建 → pack-release.ps1
# 4) 提交推送
git add -A; git commit -m "vX.Y.Z: 简述"; git push
# 5) 发布（gh 已登录 llm147369）
gh release create vX.Y.Z "publish\GlassBoard_vX.Y.Z_User.zip" --repo llm147369/GlassBoard-Source --title "vX.Y.Z" --notes "..."
# 覆盖已有 tag 的资产（次更新）：
gh release upload vX.Y.Z "publish\GlassBoard_vX.Y.Z_User.zip" --clobber
```

## 关键坑（踩过的）

1. **DOTNET_STARTUP_HOOKS**：本机环境变量指向 `C:\Users\32342\AppData\Local\GlassDeskPlusPatch\GlassDeskPatch.dll`，会让 dotnet CLI 启动崩溃（PresentationFramework 加载失败）。构建前必须 `Remove-Item Env:\DOTNET_STARTUP_HOOKS`。
2. **文件编码**：源文件有 UTF-8 也有历史 GBK 残留。`IslandWindow.cs` 等大文件用 edit 工具前先确认编码；PowerShell 读写必须显式指定编码（UTF8 BOM 用于 .ps1；README 是 UTF-8）。曾发生 edit 工具把 GBK 文件按 UTF-8 重写导致 748 个编译错误的灾难——改大文件前先 `git status` 确认有干净基线。
3. **.gitignore 忽略 dll/json**：根目录运行依赖（NAudio 全家桶、Microsoft.Win32.Registry.dll、System.Security.*、WebView2*.dll）与 `DefaultConfig.json`、`wallpapers\wallpapers.json` 都被 gitignore 拦着。发布包靠 pack-release.ps1 从构建输出+根目录组装；新 clone 的机器缺这些文件时，从最新 release zip 提取补齐。DefaultConfig.json 与 wallpapers.json 已 force-add 进 git。
4. **更新器资产名正则**：`GlassBoard_v(\d+\.\d+\.\d+(\.\d+)?)_User.zip`（UpdateRules.cs）。发布包必须用这个名字。
5. **中文路径**：更新器重启脚本必须纯 ASCII + 环境变量注入路径（GB_EXTRACT/GB_APPDIR），勿把路径写进 .cmd 文本。0.9.21.3/0.9.21.4 中文路径老用户需手动覆盖一次。
6. **视频壁纸**：WebView2 内核；窗口 SetParent 挂 Progman 的 WorkerW 后必须 `SetWindowLong(GWL_STYLE) 转 WS_CHILD`（去掉 WS_POPUP），否则 Win10/11 会把窗口拉回顶层。Chromium 需 `--disable-features=CalculateNativeWinOcclusion...` 否则壁纸层被当后台暂停合成（黑屏）。Win10 缺 WebView2 运行时在设置页有常驻提示。
7. **换装必须停进程**：exe 运行中复制会 IOException。
8. **用户环境**：用户开发机 = 本机（D:\软件\GlassBoard-Source，2560×1440，D 盘为数据盘）；用户测试机曾为 Win10 1280×720。视频壁纸测试文件在 `D:\Users\32342\Downloads\`（注意与 `D:\软件\` 是两个盘符根）。

## 运行时数据

- 配置：`%LOCALAPPDATA%\GlassBoard\layout.json`
- 日志：`%LOCALAPPDATA%\GlassBoard\session.log`、`error.log`（Store.Trace / Store.LogError 双写）
- 图标/歌词/天气缓存：同目录下各子目录，删了会自动重建
- 历史配置备份：`D:\软件\GlassBoard-ConfigBackup-2026-11-15.zip`

## 主要源码地图（src\GlassBoard\）

| 文件 | 职责 |
|---|---|
| Controller.cs | 全局编排、设置页、托盘、系统入口（4000+ 行） |
| DockWindow.cs | Dock 栏：悬停放大引擎（渲染帧驱动）、倒影、图标管线（7000+ 行） |
| GlassWindow.cs | 玻璃窗口基类：捕获/烘焙/材质/LiveBlur |
| GlassCapture.cs / WallpaperSource.cs | 壁纸层捕获、静态壁纸渲染 |
| GlassMaterials.cs | BoxBlur/Bake/BakeLive/折射（CPU 位图管线） |
| VideoWallpaper.cs | 视频动态壁纸（WebView2 + WorkerW 挂载 + watchdog） |
| IslandWindow.cs / IslandOptions.cs / IslandLyrics.cs | 灵动岛 + 歌词引擎（三源并行抢答 + 锚点插值） |
| WidgetWindow.cs / WidgetRules.cs / WidgetOptions.cs | 桌面组件（含音乐律动 6 样式 + 10 配色） |
| IslandAudio.cs | NAudio WASAPI 采集（引用计数共享，律动+灵动岛共用，静默看门狗） |
| MusicEngine.cs | GSMTC 音乐会话（Position 锚点插值） |
| RunningWindows.cs | 窗口枚举/激活/最小化（入口点击语义） |
| PanelDismissGuard.cs | 面板点击外部收起（LL 钩子 + P/Invoke 全部带 EntryPoint） |
| OnlineUpdate.cs | 在线更新（robocopy 只增不删，Release 时间戳判次更新） |
| pack-release.ps1 | 发布打包脚本（根目录） |
| zoom-test.ps1 / zoom-test-out\ | Dock 悬停自动化测试（zoom-diag.on 触发逐帧 CSV） |
