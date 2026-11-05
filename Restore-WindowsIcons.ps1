#requires -version 5.1

$ErrorActionPreference = 'Stop'

$taskManager = Get-Process -Name 'Taskmgr' -ErrorAction SilentlyContinue
if ($taskManager) {
    Write-Host '请先关闭任务管理器，再重新运行此脚本。' -ForegroundColor Yellow
    exit 2
}

$timeStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $env:LOCALAPPDATA "WindowsIconRepair\$timeStamp"
$cacheBackup = Join-Path $backupRoot 'cache'
$advancedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$explorerWasRunning = [bool](Get-Process -Name 'explorer' -ErrorAction SilentlyContinue)
$movedFiles = [System.Collections.Generic.List[object]]::new()
$oldHideIcons = $null
$hadHideIcons = $false
$registryChanged = $false

New-Item -ItemType Directory -Path $cacheBackup -Force | Out-Null

try {
    $advanced = Get-ItemProperty -Path $advancedKey -ErrorAction SilentlyContinue
    if ($null -ne $advanced -and $advanced.PSObject.Properties.Name -contains 'HideIcons') {
        $hadHideIcons = $true
        $oldHideIcons = [int]$advanced.HideIcons
    }

    [pscustomobject]@{
        CreatedAt = (Get-Date).ToString('o')
        RegistryKey = $advancedKey
        HadHideIconsValue = $hadHideIcons
        HideIconsValue = $oldHideIcons
        ExplorerWasRunning = $explorerWasRunning
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupRoot 'state.json') -Encoding UTF8

    if ($explorerWasRunning) {
        Stop-Process -Name 'explorer' -Force
        $limit = (Get-Date).AddSeconds(10)
        while ((Get-Process -Name 'explorer' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $limit) {
            Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Name 'explorer' -ErrorAction SilentlyContinue) {
            throw 'Explorer 仍在运行，无法安全重建图标缓存。'
        }
    }

    if (-not (Test-Path -LiteralPath $advancedKey)) {
        New-Item -Path $advancedKey -Force | Out-Null
    }
    if ($hadHideIcons) {
        Set-ItemProperty -Path $advancedKey -Name 'HideIcons' -Value 0
    }
    else {
        New-ItemProperty -Path $advancedKey -Name 'HideIcons' -PropertyType DWord -Value 0 -Force | Out-Null
    }
    $registryChanged = $true

    $cacheFiles = [System.Collections.Generic.List[string]]::new()
    $legacyCache = Join-Path $env:LOCALAPPDATA 'IconCache.db'
    if (Test-Path -LiteralPath $legacyCache -PathType Leaf) {
        $cacheFiles.Add($legacyCache)
    }
    $explorerCacheDir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
    if (Test-Path -LiteralPath $explorerCacheDir -PathType Container) {
        Get-ChildItem -LiteralPath $explorerCacheDir -Filter 'iconcache*.db' -File -Force |
            ForEach-Object { $cacheFiles.Add($_.FullName) }
    }

    foreach ($cacheFile in ($cacheFiles | Select-Object -Unique)) {
        $destination = Join-Path $cacheBackup ([IO.Path]::GetFileName($cacheFile))
        if (Test-Path -LiteralPath $destination) {
            $destination = Join-Path $cacheBackup ([Guid]::NewGuid().ToString('N') + '-' + [IO.Path]::GetFileName($cacheFile))
        }
        Move-Item -LiteralPath $cacheFile -Destination $destination
        $movedFiles.Add([pscustomobject]@{ Original = $cacheFile; Backup = $destination })
    }

    $movedFiles | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupRoot 'moved-cache-files.json') -Encoding UTF8

    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe')
    Write-Host '已重建 Windows 图标缓存并重启资源管理器。现在可以重新打开任务管理器检查图标。' -ForegroundColor Green
    Write-Host "备份位置：$backupRoot"
    Write-Host '如果图标没有恢复，请重启 Windows 后再检查。'
}
catch {
    Write-Host "恢复过程中出错：$($_.Exception.Message)" -ForegroundColor Red

    foreach ($item in $movedFiles) {
        if ((Test-Path -LiteralPath $item.Backup) -and -not (Test-Path -LiteralPath $item.Original)) {
            try { Move-Item -LiteralPath $item.Backup -Destination $item.Original } catch { }
        }
    }

    if ($registryChanged) {
        try {
            if ($hadHideIcons) {
                Set-ItemProperty -Path $advancedKey -Name 'HideIcons' -Value $oldHideIcons
            }
            else {
                Remove-ItemProperty -Path $advancedKey -Name 'HideIcons' -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }

    if (-not (Get-Process -Name 'explorer' -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe')
    }
    exit 1
}
