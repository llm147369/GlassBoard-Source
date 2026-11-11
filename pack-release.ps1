# pack-release.ps1 — GlassBoard 发布包打包脚本
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File pack-release.ps1            # 构建 Release 并打包
#   powershell -ExecutionPolicy Bypass -File pack-release.ps1 -SkipBuild # 跳过构建，直接打包现有输出
#   powershell -ExecutionPolicy Bypass -File pack-release.ps1 -Version 0.9.21.4   # 覆盖版本号
#
# 产物:
#   publish\GlassBoard_v<版本>_User.zip   （同名旧包自动备份到 publish\.bak\）
#   publish\GlassBoard_<日期>_Release.zip （带日期的副本）
#
# 打包内容（与在线更新器 robocopy 增量模型对齐，只增不删）:
#   GlassBoard.exe / .exe.config / .pdb
#   Interop.UIAutomationClient.dll、Microsoft.Win32.Registry.dll、
#   System.Security.AccessControl.dll、System.Security.Principal.Windows.dll
#   NAudio.dll + NAudio.*.dll（Core/Asio/Midi/WinForms/WinMM/Wasapi）
#   lib\NPSMLib.dll
#   data\AppGroups.tsv                     —— 应用自动分组（v0.9.21.3 曾漏打）
#   DefaultConfig.json                     —— 首启推荐配置（漏打则 ConfigImport 静默失效）
#   licenses\                              —— 第三方许可
#   说明.txt / README.md / THIRD_PARTY_NOTICES.md
#   wallpapers\                            —— 官方图库源文件（体积很小，默认包含）
#
# 说明:
#   - 推荐配置源优先级：仓库根 DefaultConfig.json → _release-check\extract\DefaultConfig.json（旧版兜底）
#     想推新推荐配置时，把文件放仓库根目录并命名为 DefaultConfig.json（不带 .done）即可。
#   - 本机若装有 GlassDeskPlusPatch（DOTNET_STARTUP_HOOKS 指向其 dll），dotnet CLI 会崩溃，
#     脚本构建阶段自动移除该环境变量，不影响其他程序。

param(
    [string]$Version = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repo

function Fail([string]$msg) { Write-Host "[PACK][错误] $msg" -ForegroundColor Red; exit 1 }

# ---------- 0. 环境：去掉会搞崩 SDK 的启动钩子 ----------
if ($env:DOTNET_STARTUP_HOOKS) {
    Write-Host "[PACK] 移除 DOTNET_STARTUP_HOOKS=$env:DOTNET_STARTUP_HOOKS（避免 SDK 崩溃）"
    Remove-Item Env:\DOTNET_STARTUP_HOOKS -ErrorAction SilentlyContinue
}

# ---------- 1. 版本号 ----------
if ([string]::IsNullOrWhiteSpace($Version)) {
    $asm = Join-Path $repo "src\Properties\AssemblyInfo.cs"
    if (-not (Test-Path $asm)) { Fail "找不到 $asm" }
    $m = Select-String -Path $asm -Pattern 'AssemblyFileVersion\("([^"]+)"\)' | Select-Object -First 1
    if (-not $m) { Fail "无法从 AssemblyInfo.cs 解析版本号，请用 -Version 指定" }
    $Version = $m.Matches[0].Groups[1].Value
}
Write-Host "[PACK] 版本: $Version"

# ---------- 2. 构建 ----------
$proj = Join-Path $repo "src\GlassBoard.csproj"
$out  = Join-Path $repo "src\bin\Release\net48"
if (-not $SkipBuild) {
    Write-Host "[PACK] dotnet build -c Release ..."
    dotnet build $proj -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { Fail "构建失败 (exit $LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $out "GlassBoard.exe"))) { Fail "构建输出缺失: $out\GlassBoard.exe" }

# ---------- 3. 组装 staging ----------
$stage = Join-Path $repo "_release-check\staging-latest"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

$required = @(
    "GlassBoard.exe", "GlassBoard.exe.config", "GlassBoard.pdb",
    "Interop.UIAutomationClient.dll", "Microsoft.Win32.Registry.dll",
    "System.Security.AccessControl.dll", "System.Security.Principal.Windows.dll",
    "NAudio.dll", "NAudio.Core.dll", "NAudio.Asio.dll", "NAudio.Midi.dll",
    "NAudio.WinForms.dll", "NAudio.WinMM.dll", "NAudio.Wasapi.dll",
    "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll",
    "Microsoft.Web.WebView2.Wpf.dll", "WebView2Loader.dll"
)
foreach ($f in $required) {
    $src = Join-Path $out $f
    if (-not (Test-Path $src)) { Fail "构建输出缺文件: $f" }
    Copy-Item $src $stage
}
if (Test-Path (Join-Path $out "lib\NPSMLib.dll")) {
    Copy-Item (Join-Path $out "lib") $stage -Recurse
} else { Fail "构建输出缺 lib\NPSMLib.dll" }

# 数据文件（v0.9.21.3 曾漏打，务必检查）
Copy-Item (Join-Path $repo "data") $stage -Recurse
if (-not (Test-Path (Join-Path $stage "data\AppGroups.tsv"))) { Fail "staging 缺 data\AppGroups.tsv" }

# 推荐配置
$cfg = Join-Path $repo "DefaultConfig.json"
if (-not (Test-Path $cfg)) { $cfg = Join-Path $repo "_release-check\extract\DefaultConfig.json" }
if (Test-Path $cfg) {
    Copy-Item $cfg (Join-Path $stage "DefaultConfig.json")
    Write-Host "[PACK] DefaultConfig.json 来源: $cfg"
} else {
    Write-Host "[PACK][警告] 没有可打包的 DefaultConfig.json —— 新装用户将收不到首启推荐配置。" -ForegroundColor Yellow
}

foreach ($d in @("licenses", "wallpapers")) {
    if (Test-Path (Join-Path $repo $d)) { Copy-Item (Join-Path $repo $d) $stage -Recurse }
}
foreach ($f in @("说明.txt", "README.md", "THIRD_PARTY_NOTICES.md")) {
    if (Test-Path (Join-Path $repo $f)) { Copy-Item (Join-Path $repo $f) $stage }
}

# ---------- 4. 打包 ----------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $repo "publish\GlassBoard_v${Version}_User.zip"
New-Item -ItemType Directory -Path (Split-Path $zip) -Force | Out-Null
if (Test-Path $zip) {
    $bakDir = Join-Path (Split-Path $zip) ".bak"
    New-Item -ItemType Directory -Path $bakDir -Force | Out-Null
    $bak = Join-Path $bakDir ("GlassBoard_v{0}_User.zip.bak-{1}" -f $Version, (Get-Date -Format "yyyyMMdd-HHmmss"))
    Move-Item $zip $bak
    Write-Host "[PACK] 旧包已备份: $bak"
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zip2 = Join-Path $repo ("publish\GlassBoard_{0}_Release.zip" -f (Get-Date -Format "yyyy-MM-dd"))
Copy-Item $zip $zip2 -Force

# ---------- 5. 校验 ----------
Write-Host ""
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
$entries = $z.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
$check = @("GlassBoard.exe", "GlassBoard.exe.config", "lib/NPSMLib.dll", "data/AppGroups.tsv", "DefaultConfig.json", "licenses/NPSMLib_LICENSE.txt", "说明.txt", "wallpapers/wallpapers.json", "Microsoft.Web.WebView2.Core.dll", "WebView2Loader.dll")
$missing = @($check | Where-Object { $entries -notcontains $_ })
$z.Dispose()
if ($missing.Count -gt 0) {
    Write-Host "[PACK][错误] 包内缺失: $($missing -join ', ')" -ForegroundColor Red
    exit 1
}
$size = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "[PACK] 完成: $zip  (${size} MB, $($entries.Count) 个文件)" -ForegroundColor Green
Write-Host "[PACK] 副本: $zip2"
Write-Host "[PACK] 校验通过：关键文件齐全。上传此 zip 到 GitHub Release 即可（同名版本会按时间戳触发次更新）。" -ForegroundColor Green
exit 0
