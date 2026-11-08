#Requires -Version 5.1
<#
  GlassBoard Dock 悬停放大检测脚本
  ================================
  原理：先让 Dock 开启逐帧诊断（%LOCALAPPDATA%\GlassBoard\zoom-diag.on），
  帧循环会把每帧状态（帧时刻/帧间隔/鼠标X/各图标放大倍数@屏幕中心X）写入
  zoom-diag.csv。本脚本随后用 SetCursorPos 驱动鼠标做固定编排：
    慢扫 → 悬停 → 快速甩到另一头 → 甩回 → 慢扫回，
  并在关键帧截屏（PNG）。最后离线分析 CSV 给出五项判定：
    A 帧间隔（卡顿）      B 对端串扰（悬浮左端时右端图标是否被带动）
    C 快速滑过后回落延迟  D 单帧跳变（台阶/量化）  E 放大上限
  用法：.\zoom-test.ps1 [-OutDir 目录]；本脚本会接管鼠标约 10 秒，请勿操作。
#>
param(
  [int]$HoldMs = 600,
  [string]$OutDir = ""
)
$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ZoomTestNative
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
Add-Type -AssemblyName System.Drawing

$flag = Join-Path $env:LOCALAPPDATA "GlassBoard\zoom-diag.on"
$csv  = Join-Path $env:LOCALAPPDATA "GlassBoard\zoom-diag.csv"

# ---------- 1) 找 Dock 窗口 ----------
$script:dockRect = $null
$script:dockHwnd = [IntPtr]::Zero
$cb = [ZoomTestNative+EnumProc]{
  param($h, $lp)
  $sb = New-Object System.Text.StringBuilder 256
  [void][ZoomTestNative]::GetWindowText($h, $sb, 256)
  if ($sb.ToString() -eq "GlassBoard Dock" -and [ZoomTestNative]::IsWindowVisible($h)) {
    $r = New-Object ZoomTestNative+RECT
    [void][ZoomTestNative]::GetWindowRect($h, [ref]$r)
    $script:dockRect = @{ Left = $r.Left; Top = $r.Top; Right = $r.Right; Bottom = $r.Bottom }
    $script:dockHwnd = $h
  }
  return $true
}
[void][ZoomTestNative]::EnumWindows($cb, [IntPtr]::Zero)
if (-not $script:dockRect) {
  Write-Host "FAIL: 未找到可见的 GlassBoard Dock 窗口（标题须为 'GlassBoard Dock'）"
  exit 1
}
$r = $script:dockRect
Write-Host ("Dock 窗口: ({0},{1})-({2},{3})  {4}x{5}" -f $r.Left, $r.Top, $r.Right, $r.Bottom, ($r.Right - $r.Left), ($r.Bottom - $r.Top))

# ---------- 1.5) 若 Dock 被其他窗口盖住（如最大化浏览器），临时把它抬到置顶层 ----------
$script:dockLifted = $false
function EnsureDockOnTop() {
  $pt = New-Object ZoomTestNative+POINT
  $pt.X = [int](($script:dockRect.Left + $script:dockRect.Right) / 2)
  $pt.Y = [int]($script:dockRect.Top + ($script:dockRect.Bottom - $script:dockRect.Top) * 0.6)
  $hit = [ZoomTestNative]::WindowFromPoint($pt)
  if ($hit -ne $script:dockHwnd) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][ZoomTestNative]::GetWindowText($hit, $sb, 256)
    Write-Host ("注意: Dock 被窗口 '0x{0:X} {1}' 盖住，临时抬到置顶层测试（结束后还原）" -f $hit.ToInt64(), $sb.ToString())
    [void][ZoomTestNative]::SetWindowPos($script:dockHwnd, [ZoomTestNative]::HWND_TOPMOST, 0, 0, 0, 0, [ZoomTestNative]::SWP_NOMOVE -bor [ZoomTestNative]::SWP_NOSIZE -bor [ZoomTestNative]::SWP_NOACTIVATE -bor [ZoomTestNative]::SWP_SHOWWINDOW)
    $script:dockLifted = $true
  }
}
function RestoreDockBand() {
  if ($script:dockLifted) {
    [void][ZoomTestNative]::SetWindowPos($script:dockHwnd, [ZoomTestNative]::HWND_NOTOPMOST, 0, 0, 0, 0, [ZoomTestNative]::SWP_NOMOVE -bor [ZoomTestNative]::SWP_NOSIZE -bor [ZoomTestNative]::SWP_NOACTIVATE)
    $script:dockLifted = $false
  }
}
EnsureDockOnTop

# ---------- 2) 输出目录 ----------
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "zoom-test-out" }
$runDir = Join-Path $OutDir ("run-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$shots  = Join-Path $runDir "shots"
New-Item -ItemType Directory -Force -Path $shots | Out-Null
Write-Host ("输出目录: " + $runDir)

function Snap([string]$name) {
  $rr = $script:dockRect
  $x = [Math]::Max(0, $rr.Left - 160)
  $y = [Math]::Max(0, $rr.Top - 190)
  $w = $rr.Right - $rr.Left + 320
  $h = $rr.Bottom - $rr.Top + 250
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  $bmp.Save((Join-Path $shots $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
function MoveTo([int]$x, [int]$y) { [void][ZoomTestNative]::SetCursorPos($x, $y) }
function SleepMs([int]$ms) { Start-Sleep -Milliseconds $ms }
function Sweep([int]$fromX, [int]$toX, [int]$stepMs, [int]$px) {
  $dist = $toX - $fromX
  $steps = [Math]::Max(1, [int]([Math]::Abs($dist) / $px))
  $sx = $dist / $steps
  for ($s = 0; $s -le $steps; $s++) {
    MoveTo ([int]($fromX + $sx * $s)) $script:mouseY
    SleepMs $stepMs
  }
}
function Flick([int]$fromX, [int]$toX) {
  for ($j = 1; $j -le 4; $j++) {
    MoveTo ([int]($fromX + ($toX - $fromX) * $j / 4.0)) $script:mouseY
    SleepMs 10
  }
}

# ---------- 3) 开启诊断 + 校准图标中心 ----------
if (Test-Path $csv) { Move-Item $csv (Join-Path $runDir "zoom-diag-prev.csv") -Force }
New-Item -ItemType File -Force -Path $flag | Out-Null
$script:mouseY = [int]($r.Top + ($r.Bottom - $r.Top) * 0.6)
$midX = [int](($r.Left + $r.Right) / 2)
MoveTo $midX $script:mouseY
$calLine = $null
for ($i = 0; $i -lt 40; $i++) {
  if (Test-Path $csv) {
    $lines = @(Get-Content $csv)
    if ($lines.Count -ge 1) {
      $p = $lines[$lines.Count - 1] -split '\|'
      if ($p.Count -ge 6) { $calLine = $lines[$lines.Count - 1]; break }
    }
  }
  SleepMs 100
}
if (-not $calLine) {
  Write-Host "FAIL: 诊断数据未出现（Dock 未响应悬停？确认动效开启且 Zoom>1）"
  Remove-Item $flag -ErrorAction SilentlyContinue
  RestoreDockBand
  exit 1
}
$p = $calLine -split '\|'
$calXs = @()
for ($i = 3; $i -lt $p.Count; $i++) {
  $q = $p[$i] -split '@'
  $calXs += [int]$q[1]
}
$centers = @($calXs | Sort-Object)
$n = $centers.Count
Write-Host ("校准完成: {0} 个图标，最左中心 X={1}，最右中心 X={2}" -f $n, $centers[0], $centers[$n-1])

# ---------- 4) 鼠标编排 ----------
MoveTo $midX ($r.Top - 40)          # 移出 Dock，让所有图标归位
SleepMs 300
$startX = $centers[0] - 8
$target6 = $centers[[Math]::Min(5, $n - 1)]
$rightT  = $centers[$n - 3]
Write-Host "编排开始：慢扫 → 悬停 → 快甩 → 甩回 → 慢扫"
MoveTo $startX $script:mouseY
SleepMs 400
Sweep $startX $target6 10 4
SleepMs $HoldMs
Snap "01-hover-left.png"
Flick $target6 $rightT
SleepMs 30;  Snap "02-flick+030ms.png"
SleepMs 120; Snap "03-flick+150ms.png"
SleepMs 300; Snap "04-flick+450ms.png"
SleepMs $HoldMs
Flick $rightT $centers[1]
SleepMs 100; Snap "05-return+100ms.png"
SleepMs 500
Sweep $centers[1] $startX 10 4
SleepMs 500
Snap "06-end-left.png"
Write-Host "编排结束"

# ---------- 5) 关闭诊断 + 归档 CSV ----------
Remove-Item $flag -ErrorAction SilentlyContinue
SleepMs 900
Move-Item $csv (Join-Path $runDir "zoom-diag.csv") -Force
if (Test-Path $csv) { Remove-Item $csv -Force -ErrorAction SilentlyContinue }
RestoreDockBand

# ---------- 6) 分析 ----------
$rows = @()
foreach ($ln in (Get-Content (Join-Path $runDir "zoom-diag.csv"))) {
  $p = $ln -split '\|'
  if ($p.Count -lt 6) { continue }
  $nodes = @()
  for ($i = 3; $i -lt $p.Count; $i++) {
    $q = $p[$i] -split '@'
    $nodes += , @([double]$q[0], [int]$q[1])
  }
  $rows += , @([int]$p[0], [int]$p[1], [int]$p[2], $nodes)
}
if ($rows.Count -lt 10) { Write-Host "FAIL: 诊断行数不足 ($($rows.Count))"; exit 1 }
$nodeCount = $rows[0][3].Count

# 每个节点的中位屏幕中心 X → 视觉顺序
$medX = New-Object double[] $nodeCount
for ($i = 0; $i -lt $nodeCount; $i++) {
  $xs = @($rows | ForEach-Object { $_[3][$i][1] } | Sort-Object)
  $medX[$i] = $xs[[int]($xs.Count / 2)]
}
$order = @(0..($nodeCount-1) | Sort-Object { $medX[$_] })
$rightIdx = @($order[$nodeCount-3], $order[$nodeCount-2], $order[$nodeCount-1])
$leftEdge = $medX[$order[0]]

# A 帧间隔
$dts = @($rows | ForEach-Object { $_[1] } | Sort-Object)
$p95 = $dts[[int]($dts.Count * 0.95)]
$maxDt = ($rows | ForEach-Object { $_[1] } | Measure-Object -Maximum).Maximum

# B 对端串扰：只在“最后一次鼠标突跳（甩回左端）+450ms 之后”的稳定左端悬停期里，
# 取最右 3 个节点的最大放大——排除甩回瞬间右端图标仍在自然回落（重力感）的残影。
# 突跳判定只按 |Δ鼠标X|≥150（慢扫每行最多 ~20px，绝不会误判；停顿期不产生行，
# 行间时间差会包含停顿，故不设时间条件）。
$lastJump = -1
for ($i = 1; $i -lt $rows.Count; $i++) {
  $dmx = [Math]::Abs($rows[$i][2] - $rows[$i-1][2])
  if ($dmx -ge 150) { $lastJump = $i }
}
$bFrom = if ($lastJump -ge 0) { [Math]::Min($rows.Count - 6, $lastJump + 30) } else { [Math]::Max(0, $rows.Count - 20) }
$leftMax = 0.0
for ($k = $bFrom; $k -lt $rows.Count; $k++) {
  $row = $rows[$k]
  if ($row[2] -le ($leftEdge + 220)) {
    foreach ($ix in $rightIdx) {
      if ($row[3][$ix][0] -gt $leftMax) { $leftMax = $row[3][$ix][0] }
    }
  }
}

# C 快速滑过后回落延迟：真正的左→右快甩 = 鼠标 X 突跳 ≥150px 且落点在右半区
#（排除开场的“瞬移到最左”与“甩回最左”两次向左突跳；不设时间条件，见 B 注释）。
$flickAt = -1
for ($i = 1; $i -lt $rows.Count; $i++) {
  $dmx = $rows[$i][2] - $rows[$i-1][2]
  if ($dmx -ge 150 -and $rows[$i][2] -ge ($leftEdge + 220)) { $flickAt = $i; break }
}
$dropMs = -1
if ($flickAt -ge 0) {
  $from = [Math]::Max(0, $flickAt - 30)
  $bestIdx = 0; $bestCur = 0.0
  for ($k = $from; $k -lt $flickAt; $k++) {
    for ($m = 0; $m -lt $nodeCount; $m++) {
      if ($rows[$k][3][$m][0] -gt $bestCur) { $bestCur = $rows[$k][3][$m][0]; $bestIdx = $m }
    }
  }
  for ($k = $flickAt; $k -lt $rows.Count; $k++) {
    if ($rows[$k][3][$bestIdx][0] -le 1.10) { $dropMs = $rows[$k][0] - $rows[$flickAt][0]; break }
  }
}

# D 单帧跳变（归一化到 16ms）+ 帧间隔多样性：指数平滑的天然最大步进≈0.46
#（2.0 节点在 9ms 快帧上回落一步）；时钟量化 bug（DateTime 15.6ms 档位）会让
# dt 只有 0/15.6/31.2 三种值且步进 >1.0。两步一起判。
$maxStep = 0.0
for ($i = 1; $i -lt $rows.Count; $i++) {
  $dt = [Math]::Max(1, $rows[$i][1])
  for ($m = 0; $m -lt $nodeCount; $m++) {
    # 屏幕中心 X 突变 >25px = 列重排（Refresh 后按视觉 X 排序），跳过该跨列伪跳变
    if ([Math]::Abs($rows[$i][3][$m][1] - $rows[$i-1][3][$m][1]) -gt 25) { continue }
    $d = [Math]::Abs($rows[$i][3][$m][0] - $rows[$i-1][3][$m][0]) * 16.0 / $dt
    if ($d -gt $maxStep) { $maxStep = $d }
  }
}
$distinctDt = @($rows | ForEach-Object { $_[1] } | Sort-Object -Unique).Count

# E 最大放大倍数
$maxCur = 0.0
foreach ($row in $rows) { foreach ($nd in $row[3]) { if ($nd[0] -gt $maxCur) { $maxCur = $nd[0] } } }

# ---------- 7) 判定输出 ----------
function Verdict([string]$name, [bool]$pass, [string]$detail) {
  $tag = if ($pass) { "PASS" } else { "FAIL" }
  Write-Host ("[{0}] {1}: {2}" -f $tag, $name, $detail)
}
Write-Host ""
Write-Host ("诊断行数 {0}，图标 {1} 个，检测到鼠标突跳 {2} 次（首个突跳第 {3} 行）" -f $rows.Count, $nodeCount, $(@($flickAt -ne -1).Count), $flickAt)
Verdict "A 帧间隔(卡顿)"       ($maxDt -le 150 -and $p95 -le 66) ("p95={0}ms 最大={1}ms（p95≤66 即≥15fps 且 最大≤150；dt 自适应平滑会吸收帧滑移）" -f $p95, $maxDt)
Verdict "B 对端串扰"           ($leftMax -le 1.05)              ("悬浮左端时最右3个图标最大放大 {0:F2}（应≤1.05）" -f $leftMax)
if ($dropMs -ge 0) {
  Verdict "C 快甩回落延迟"     ($dropMs -le 250)                ("{0}ms（应≤250ms）" -f $dropMs)
} else {
  Verdict "C 快甩回落延迟"     $false                          "未检测到有效突跳或回落"
}
Verdict "D 单帧跳变(台阶)"     (($maxStep -le 0.55) -and ($distinctDt -ge 8)) ("16ms 归一最大步进 {0:F3}（应≤0.55），dt 不同取值 {1} 种（应≥8）" -f $maxStep, $distinctDt)
Verdict "E 放大上限"           ($maxCur -le 2.05)               ("实测最大 {0:F2}（应≤2.0）" -f $maxCur)
Write-Host ""
Write-Host ("截图与原始数据: " + $runDir)
