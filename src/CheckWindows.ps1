$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
$root=Split-Path -Parent $PSScriptRoot
$lines=New-Object Collections.Generic.List[string]
$lines.Add('GlassBoard v'+[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'GlassBoard.exe')).FileVersion+' Windows check')
$lines.Add('OS: '+[Environment]::OSVersion)
$lines.Add('64-bit OS: '+[Environment]::Is64BitOperatingSystem)
foreach($screen in [Windows.Forms.Screen]::AllScreens){$lines.Add('Display: '+$screen.DeviceName+' Primary='+$screen.Primary+' Bounds='+$screen.Bounds+' WorkingArea='+$screen.WorkingArea)}
$process=Start-Process -FilePath (Join-Path $root 'GlassBoard.exe') -ArgumentList '--self-test' -PassThru -Wait
$lines.Add('WPF self-test exit code: '+$process.ExitCode)
$result=Join-Path $root 'self-test-result.txt'
if(Test-Path $result){$lines.Add((Get-Content -Raw -LiteralPath $result))}
$lines.Add('Manual v0.9.17 acceptance still required: Win+D (keyboard/taskbar corner/repeated toggle), Explorer restart, orb click/drag/outside click/Esc, real Wi-Fi/Bluetooth/audio/brightness, lightweight Windows PowerShell controls (no engine directory),  reopen widgets, restart layout, 100/125/150/200% scaling, multiple displays, drag/hover/undo, real player controls and cover, NVIDIA desktop recording and Instant Replay.')
$destination=Join-Path $root 'WindowsCheck-result.txt'
$lines | Set-Content -LiteralPath $destination -Encoding UTF8
Write-Host ('Saved: '+$destination)
