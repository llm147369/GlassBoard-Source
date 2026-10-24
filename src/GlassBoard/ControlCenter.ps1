# GlassBoard 控制中心桥接脚本
# 通过 WinRT（Windows.Devices.Radios / Bluetooth / Enumeration）读写无线电与蓝牙配对信息，
# 与主程序之间使用 JSON 行协议（每请求一行 JSON，每回复一行 JSON）。
$ErrorActionPreference = 'SilentlyContinue'

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

function Await($op, $resultType) {
    $t = $asTaskGeneric.MakeGenericMethod($resultType)
    $task = $t.Invoke($null, @($op))
    $task.Wait(-1) | Out-Null
    return $task.Result
}

[Windows.Devices.Radios.Radio, Windows.Devices.Radios, ContentType = WindowsRuntime] | Out-Null
[Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType = WindowsRuntime] | Out-Null
[Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType = WindowsRuntime] | Out-Null

function Get-Radio([int]$kind) {
    try {
        $listType = [System.Collections.Generic.IReadOnlyList``1].MakeGenericType([Windows.Devices.Radios.Radio])
        $radios = Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) $listType
        return ($radios | Where-Object { $_.Kind -eq $kind } | Select-Object -First 1)
    }
    catch {
        return $null
    }
}

function Radio-State($r) {
    if ($null -eq $r) { return 'absent' }
    if ($r.State -eq [Windows.Devices.Radios.RadioState]::On) { return 'on' }
    return 'off'
}

$wifi = Get-Radio 1   # RadioKind.WiFi
$bt = Get-Radio 3     # RadioKind.Bluetooth

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    $ok = $false
    $err = ''
    $value = ''
    try {
        $req = $line | ConvertFrom-Json
        switch ($req.Action) {
            'exit' {
                break
            }
            'state' {
                $ok = $true
                $value = 'wifi:' + (Radio-State $wifi) + '|bt:' + (Radio-State $bt)
            }
            'radio' {
                $target = $null
                if ($req.Arg1 -eq 'wifi') { $target = $wifi } elseif ($req.Arg1 -eq 'bt') { $target = $bt }
                if ($null -eq $target) { throw '无线电设备不存在或不受支持' }
                $st = [Windows.Devices.Radios.RadioState]::Off
                if ($req.Arg2 -eq 'on') { $st = [Windows.Devices.Radios.RadioState]::On }
                $status = Await ($target.SetStateAsync($st)) ([Windows.Devices.Radios.RadioAccessStatus])
                $ok = ($status -eq [Windows.Devices.Radios.RadioAccessStatus]::Allowed)
                if (-not $ok) { $err = '系统拒绝更改无线电状态（' + $status + '）' }
            }
            'bluetooth' {
                $sel = [Windows.Devices.Bluetooth.BluetoothDevice]::GetDeviceSelectorFromPairingState($true)
                $collType = [Windows.Devices.Enumeration.DeviceInformationCollection]
                $coll = Await ([Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync($sel)) $collType
                $lines = @()
                foreach ($d in $coll) {
                    $connected = $false
                    try {
                        $bd = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromIdAsync($d.Id)) ([Windows.Devices.Bluetooth.BluetoothDevice])
                        if ($null -ne $bd) {
                            $connected = ($bd.ConnectionStatus -eq [Windows.Devices.Bluetooth.BluetoothConnectionStatus]::Connected)
                        }
                    }
                    catch {
                    }
                    $name = [string]$d.Name
                    if ([string]::IsNullOrWhiteSpace($name)) { $name = '(未命名设备)' }
                    $lines += ($d.Id + "`t" + ($name -replace "`t", ' ') + "`t" + $connected)
                }
                $value = $lines -join "`n"
                $ok = $true
            }
            'unpair' {
                $bd = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromIdAsync([string]$req.Arg1)) ([Windows.Devices.Bluetooth.BluetoothDevice])
                if ($null -eq $bd) { throw '设备不存在' }
                Await ($bd.UnpairAsync()) ([bool]) | Out-Null
                $ok = $true
            }
            default {
                throw ('未知操作: ' + $req.Action)
            }
        }
    }
    catch {
        $ok = $false
        $err = $_.Exception.Message
    }
    @{ Ok = $ok; Error = $err; Value = $value } | ConvertTo-Json -Compress
}
