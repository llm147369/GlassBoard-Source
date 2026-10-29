$ErrorActionPreference = 'SilentlyContinue'
$lines = New-Object System.Collections.Generic.List[string]
$flags = New-Object System.Collections.Generic.List[string]
function Say([string]$text) { $lines.Add($text); Write-Host $text }

$gbFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GlassBoard'
$sessionLog = Join-Path $gbFolder 'session.log'
$layoutFile = Join-Path $gbFolder 'layout.json'
$iconCache = Join-Path $gbFolder 'icons'

Say '=============================================================='
Say ' GlassBoard 卡顿排查报告  ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Say ' 本脚本只读取信息，不修改任何配置与文件。'
Say '=============================================================='

# ---------- 1. GlassBoard 运行状态 ----------
Say ''
Say '---------- 1. GlassBoard 运行状态 ----------'
$gb = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -ieq 'GlassBoard.exe' })
if ($gb.Count -eq 0) {
    Say '  GlassBoard 未运行（只能分析日志与环境）。'
    $flags.Add('GlassBoard 未运行')
}
$mainIds = @()
foreach ($p in $gb) {
    $role = '主进程'
    if ($p.CommandLine -match '--netease-ui-bridge') { $role = '网易云桥接子进程' }
    elseif ($p.CommandLine -match '--self-test') { $role = '自检进程' }
    else { $mainIds += [int]$p.ProcessId }
    $cpu = ''
    try {
        $proc = [System.Diagnostics.Process]::GetProcessById([int]$p.ProcessId)
        $cpu = ' CPU=' + [math]::Round($proc.TotalProcessorTime.TotalSeconds, 1) + 's'
        Say ('  PID {0}  {1}  启动={2}  内存={3}MB{4}' -f $p.ProcessId, $role, $proc.StartTime.ToString('HH:mm:ss'), [math]::Round($proc.WorkingSet64 / 1MB, 0), $cpu)
        $proc.Dispose()
    } catch { Say ('  PID {0}  {1}（进程已变化）' -f $p.ProcessId, $role) }
}
if ($mainIds.Count -gt 0) {
    $exePath = $null
    try { $exePath = ([System.Diagnostics.Process]::GetProcessById($mainIds[0])).MainModule.FileName } catch { }
    if ($exePath) {
        $v = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
        Say ('  运行中的版本: {0}  路径: {1}' -f $v.FileVersion, $exePath)
    }
}
$helpers = @(Get-CimInstance Win32_Process | Where-Object { ($_.Name -ieq 'powershell.exe' -or $_.Name -ieq 'pwsh.exe') -and $_.CommandLine -match '(?:Native)?MediaBridge\.ps1|-EncodedCommand' })
if ($helpers.Count -gt 0) { Say ('  音乐桥接进程: ' + $helpers.Count + ' 个（powerShell 常驻属正常，被管家类工具杀进程会反复重启）') }

# ---------- 2. session.log 性能解析 ----------
Say ''
Say '---------- 2. session.log 性能解析 ----------'
if (-not (Test-Path -LiteralPath $sessionLog)) {
    Say '  无 session.log'
} else {
    $logSize = [math]::Round((Get-Item -LiteralPath $sessionLog).Length / 1MB, 2)
    Say ('  日志大小: {0} MB' -f $logSize)
    if ($logSize -gt 10) { $flags.Add('session.log 超过 10MB（写入量大，可删除让程序重建）') }
    $content = Get-Content -LiteralPath $sessionLog -Encoding UTF8
    $total = $content.Count

    # 面板打开耗时
    $board = @($content | Select-String -Pattern 'BOARD open total=(\d+)ms build=(\d+)ms backdrop=(\d+)ms' | ForEach-Object {
        [pscustomobject]@{ Total = [int]$_.Matches[0].Groups[1].Value; Build = [int]$_.Matches[0].Groups[2].Value; Backdrop = [int]$_.Matches[0].Groups[3].Value }
    })
    if ($board.Count -gt 0) {
        $maxT = ($board | Measure-Object Total -Maximum).Maximum
        $avgT = [math]::Round(($board | Measure-Object Total -Average).Average, 0)
        $maxB = ($board | Measure-Object Build -Maximum).Maximum
        $maxD = ($board | Measure-Object Backdrop -Maximum).Maximum
        Say ('  面板打开 {0} 次: 平均 {1}ms / 最慢 {2}ms（其中磁贴重建最慢 {3}ms、背景捕获最慢 {4}ms）' -f $board.Count, $avgT, $maxT, $maxB, $maxD)
        if ($maxT -ge 600) { $flags.Add('面板打开最慢 {0}ms：此机存在明显卡顿，重建慢=磁盘/杀软，捕获慢=显卡/壁纸软件' -f $maxT) }
        elseif ($maxT -ge 350) { $flags.Add('面板打开最慢 {0}ms：中等偏慢' -f $maxT) }
        $slow = @($board | Where-Object { $_.Total -ge 300 } | Select-Object -First 5 | ForEach-Object { '    total=' + $_.Total + 'ms build=' + $_.Build + 'ms backdrop=' + $_.Backdrop + 'ms' })
        if ($slow.Count -gt 0) { Say '  最慢的 5 次:'; $slow | ForEach-Object { Say $_ } }
    } else { Say '  没有 BOARD open 行（日志太旧或从未打开过面板）' }

    # UI 线程堵塞频率
    $diagAll = @($content | Select-String -Pattern 'DIAG ')
    $diagBusy = @($diagAll | Where-Object { $_.Line -match 'busy=True' })
    if ($diagAll.Count -gt 0) {
        $ratio = [math]::Round(100 * $diagBusy.Count / $diagAll.Count, 1)
        Say ('  UI 线程忙占比: busy=True {0}/{1}（{2}%）' -f $diagBusy.Count, $diagAll.Count, $ratio)
        if ($ratio -ge 15) { $flags.Add('UI 线程忙占比 {0}%：杀软钩子或显卡驱动是头号嫌疑' -f $ratio) }
    }

    # 启动与预热
    $starts = @($content | Select-String -Pattern 'START phase=controller ms=(\d+)' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value })
    if ($starts.Count -gt 0) { Say ('  启动到控制器就绪: 最慢 {0}ms（冷启动 3s 内都算正常）' -f ($starts | Measure-Object -Maximum).Maximum) }
    $warms = @($content | Select-String -Pattern 'WARM icons done n=(\d+)' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value })
    if ($warms.Count -gt 0) { Say ('  图标预热完成记录 {0} 条（异步化后不再阻塞界面）' -f $warms.Count) }
    $inits = @($content | Select-String -Pattern 'WINDOW init cls=(\S+) ms=(\d+)')
    if ($inits.Count -gt 0) { Say ('  WINDOW init 行（数字=启动后第几秒创建窗口，不是初始化耗时）: 最近 ' + $inits.Count + ' 条') }
    $weixin = @($content | Select-String -Pattern 'WEIXIN raw totals')
    if ($weixin.Count -gt 0) { Say ('  微信事件统计行: ' + $weixin.Count + ' 条（微信自身窗口事件多属正常）') }
}

# ---------- 3. 配置负担 ----------
Say ''
Say '---------- 3. 配置与缓存负担 ----------'
if (Test-Path -LiteralPath $layoutFile) {
    $size = [math]::Round((Get-Item -LiteralPath $layoutFile).Length / 1MB, 2)
    Say ('  layout.json: {0} MB' -f $size)
    if ($size -gt 1.5) { $flags.Add('配置超过 1.5MB：每次改动同步写盘变慢') }
    $raw = Get-Content -LiteralPath $layoutFile -Raw -Encoding UTF8
    $kind = $null; $blur = $null; $motion = $null; $opacity = $null; $running = $null
    if ($raw -match '"Kind":(\d+)') { $kind = [int]$Matches[1] }
    if ($raw -match '"BlurRadius":([\d.]+)') { $blur = [double]$Matches[1] }
    if ($raw -match '"MotionEnabled":(true|false)') { $motion = $Matches[1] }
    if ($raw -match '"OpacityPercent":([\d.]+)') { $opacity = [double]$Matches[1] }
    if ($raw -match '"ShowRunning":(true|false)') { $running = $Matches[1] }
    if ($null -ne $kind) {
        $matText = if ($kind -eq 1 -or $kind -eq 3) { '渐变材质（渲染较重）' } else { '纯色材质（渲染最轻）' }
        Say ('  材质 Kind={0} → {1}' -f $kind, $matText)
        if ($kind -eq 1 -or $kind -eq 3) { $flags.Add('正在使用渐变材质：卡顿机建议外观-材质风格切纯净实色') }
    }
    if ($null -ne $blur) {
        Say ('  模糊半径: {0}' -f $blur)
        if ($blur -ge 20) { $flags.Add('模糊半径 {0}：软件模糊很吃渲染，卡顿机建议调 0' -f $blur) }
    }
    if ($null -ne $motion) { Say ('  动效: ' + $motion); if ($motion -eq 'true') { $flags.Add('动效开启：卡顿机建议外观里关闭动效') } }
    if ($null -ne $opacity) { Say ('  不透明度: ' + $opacity + '%') }
    if ($null -ne $running) { Say ('  显示运行列表: ' + $running) }
} else { Say '  无 layout.json' }

if (Test-Path -LiteralPath $iconCache) {
    $icons = @(Get-ChildItem -LiteralPath $iconCache -File)
    $bytes = ($icons | Measure-Object Length -Sum).Sum
    Say ('  图标磁盘缓存: {0} 个文件 / {1} MB' -f $icons.Count, [math]::Round($bytes / 1MB, 1))
    if ($icons.Count -lt 10) { $flags.Add('图标缓存近乎空：面板前几次打开会边用边提取，属一次性冷启动代价') }
} else { Say '  无图标缓存目录（冷启动会现场提取全部图标）'; $flags.Add('图标缓存不存在：首开面板会集中提取图标，属一次性代价') }

# ---------- 4. 系统环境 ----------
Say ''
Say '---------- 4. 系统环境 ----------'
Say ('  OS: ' + [Environment]::OSVersion.VersionString + '  64位: ' + [Environment]::Is64BitOperatingSystem)
if ($env:SESSIONNAME -match 'RDP') { $flags.Add('正在远程桌面会话：WPF 强制软件渲染，卡顿属环境必然') ; Say '  会话: 远程桌面（软件渲染）' }

$net = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
if ($net) {
    Say ('  .NET Framework 4.x Release: ' + $net.Release + '（>=528040 即 4.8）')
    if ([int]$net.Release -lt 528040) { $flags.Add('.NET 4.8 缺失，程序可能根本起不来') }
}

$gpus = @(Get-CimInstance Win32_VideoController)
foreach ($g in $gpus) {
    $driverAge = ''
    if ($g.DriverDate) {
        $days = [math]::Round(((Get-Date) - $g.DriverDate).TotalDays, 0)
        $driverAge = [string]$days + ' 天'
    }
    Say ('  显卡: {0}  驱动版本 {1}  驱动日期 {2}' -f $g.Name, $g.DriverVersion, $driverAge)
    if ($g.Name -match 'Microsoft Basic|Microsoft 基本') { $flags.Add('使用的是微软基本显示适配器：无硬件加速，必然卡') }
    elseif ($g.DriverDate -and $days -gt 730) { $flags.Add('显卡驱动过旧（' + $driverAge + '）：更新显卡驱动后再对比') }
}

$disks = @(Get-PhysicalDisk)
if ($disks.Count -gt 0) {
    foreach ($d in $disks) {
        Say ('  磁盘: {0}  类型 {1}  总线 {2}' -f $d.FriendlyName, $d.MediaType, $d.BusType)
        if ($d.MediaType -eq 'HDD') { $flags.Add('程序所在磁盘是机械盘：冷启动与写盘都慢') }
    }
}

$scheme = (powercfg /getactivescheme) -join ' '
if ($scheme -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') {
    $guid = $Matches[1]
    $name = '自定义'
    if ($guid -eq '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c') { $name = '高性能' }
    elseif ($guid -eq '381b4222-f694-41f0-9685-ff5bb260df2e') { $name = '平衡'; $flags.Add('电源计划是平衡：桌面机会积极降频，可切高性能对比') }
    elseif ($guid -eq 'a1841308-3541-4fab-bc81-f71556f20b4a') { $name = '节能'; $flags.Add('电源计划是节能：CPU 被限频，卡顿主因之一') }
    elseif ($guid -eq 'e9a42b02-d5df-448d-aa00-03f14749eb61') { $name = '卓越性能' }
    Say ('  电源计划: ' + $name)
}

$os = Get-CimInstance Win32_OperatingSystem
Say ('  内存: 可用 {0} GB / 共 {1} GB' -f [math]::Round($os.FreePhysicalMemory / 1MB, 1), [math]::Round($os.TotalVisibleMemorySize / 1MB, 1))

# 第三方安全软件进程名（联想/微软电脑管家、360、火绒、卡巴、腾讯管家等），排除联想热键/电池等工具服务
$avPattern = '360|ZhuDongFangYu|QHSafe|PcManager|PCMgr|电脑管家|HipsDaemon|HipsTray|wsctrl|sysdiag|kaspersky|avp|QQPCTray|QQPCRTP|QQPCMgr|McAfee|bdagent|avguard|avgnt|Norton|nortonsecurity'
$av = @(Get-Process | Where-Object { ($_.Name -match $avPattern) -or ($_.Description -match $avPattern) } | Sort-Object Name -Unique)
$defender = @(Get-Process | Where-Object { $_.Name -ieq 'MsMpEng' })
if ($av.Count -gt 0) {
    Say ('  第三方安全软件: ' + (($av | ForEach-Object { $_.Name }) -join ', '))
    $flags.Add('检测到第三方安全软件：实时扫描会拦截 GlassBoard 高频写盘（layout.json/session.log/图标缓存），建议加白名单')
} else {
    Say '  第三方安全软件: 未发现'
}
if ($defender.Count -gt 0) { Say '  Windows Defender: 运行中（系统自带，实时扫描同样会拖慢写盘，可加白名单）' }

# ---------- 5. 结论 ----------
Say ''
Say '---------- 5. 结论 ----------'
if ($flags.Count -eq 0) {
    Say '  未发现明确卡顿风险因素。这台机器日志与环境数值都在正常范围。'
    Say '  如果仍觉得卡，请复现卡顿时立刻重跑本脚本，并注意此时面板打开耗时是否升高。'
} else {
    Say ('  发现 ' + $flags.Count + ' 个风险因素：')
    $i = 0
    foreach ($f in $flags) { $i++; Say ('  {0}. {1}' -f $i, $f) }
    Say ''
    Say '  通用建议（按效果排序）：'
    Say '  1. 外观设置里：材质风格=纯净实色、模糊=0、关闭动效。'
    Say '  2. 把 GlassBoard 目录与 %LocalAppData%\GlassBoard 加入杀软白名单。'
    Say '  3. 更新显卡驱动；电源计划切高性能。'
    Say '  4. 若图标缓存冷启动慢，正常使用几次后会自动建立缓存，之后会明显变快。'
}

$dest = Join-Path ([Environment]::GetFolderPath('Desktop')) ('GlassBoard_LagCheck_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.txt')
try { $lines | Set-Content -LiteralPath $dest -Encoding UTF8; Say ''; Say ('报告已保存: ' + $dest) } catch { Say '报告保存失败（桌面不可写）' }
