param([ValidateRange(6,300)][int]$Seconds=30,[ValidateRange(1,10)][int]$Interval=2)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$samples=New-Object 'Collections.Generic.List[object]'
$previousCpu=@{}
$end=(Get-Date).AddSeconds($Seconds)
Write-Host 'GlassBoard CPU and memory sampling. Keep the same widgets/player/layout for comparisons.'
Write-Host 'Open or hide the panel during sampling to check retention. No process memory is modified.'
do {
    $processes=@(Get-CimInstance Win32_Process)
    $main=@($processes | Where-Object {$_.Name -ieq 'GlassBoard.exe' -and $_.CommandLine -notmatch '--netease-ui-bridge|--self-test|--restore-shell'})
    if($main.Count -eq 0){throw 'GlassBoard is not running. Start it before running this check.'}
    $ids=New-Object 'Collections.Generic.HashSet[int]'
    foreach($item in $main){[void]$ids.Add([int]$item.ProcessId)}
    do {$changed=$false;foreach($item in $processes){
        $musicHelper=(($item.Name -ieq 'powershell.exe' -or $item.Name -ieq 'pwsh.exe') -and $item.CommandLine -match '(?:Native)?MediaBridge\.ps1|-EncodedCommand') -or ($item.Name -ieq 'GlassBoard.exe' -and $item.CommandLine -match '--netease-ui-bridge')
        if($musicHelper -and $ids.Contains([int]$item.ParentProcessId) -and $ids.Add([int]$item.ProcessId)){$changed=$true}
    }}while($changed)
    $stamp=(Get-Date).ToString('o')
    foreach($targetId in $ids){
        $p=$null
        try {
            $p=[Diagnostics.Process]::GetProcessById($targetId)
            $role=if(@($main | Where-Object {[int]$_.ProcessId -eq $targetId}).Count -gt 0){'main'}else{'helper'}
            $now=[DateTime]::UtcNow;$cpu=$p.TotalProcessorTime.TotalSeconds;$cpuPercent=$null
            if($previousCpu.ContainsKey($targetId)){$old=$previousCpu[$targetId];$elapsed=($now-$old.Time).TotalSeconds;if($elapsed -gt 0 -and $cpu -ge $old.Cpu){$cpuPercent=[Math]::Round(100*($cpu-$old.Cpu)/$elapsed/[Environment]::ProcessorCount,3)}}
            $previousCpu[$targetId]=@{Time=$now;Cpu=$cpu}
            $samples.Add([pscustomobject]@{Time=$stamp;CpuPercent=$cpuPercent;PID=$targetId;Role=$role;Process=$p.ProcessName;WorkingSetMB=[Math]::Round($p.WorkingSet64/1MB,2);PrivateBytesMB=[Math]::Round($p.PrivateMemorySize64/1MB,2)})
        }catch [ArgumentException] {}catch [InvalidOperationException] {}catch [ComponentModel.Win32Exception] {}finally{if($null -ne $p){$p.Dispose()}}
    }
    if((Get-Date) -lt $end){Start-Sleep -Seconds $Interval}
}while((Get-Date) -lt $end)
$file=Join-Path $root ('MemoryReport_'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'.csv')
$samples | Export-Csv -LiteralPath $file -NoTypeInformation -Encoding UTF8
$summary=@($samples | Group-Object PID | ForEach-Object {$group=$_.Group;[pscustomobject]@{PID=$group[0].PID;Role=$group[0].Role;Process=$group[0].Process;AverageCpuPercent=[Math]::Round(($group | Where-Object {$null -ne $_.CpuPercent} | Measure-Object CpuPercent -Average).Average,3);AverageWorkingSetMB=[Math]::Round(($group | Measure-Object WorkingSetMB -Average).Average,2);PeakWorkingSetMB=($group | Measure-Object WorkingSetMB -Maximum).Maximum;PeakPrivateBytesMB=($group | Measure-Object PrivateBytesMB -Maximum).Maximum}})
$summary | Format-Table -AutoSize | Out-Host
Write-Host ('Saved: '+$file)
Write-Host 'Working set includes resident shared pages; private bytes include committed private memory. These differ from some Task Manager columns.'
Write-Host 'The sampler is excluded. Helpers are listed separately; compare identical usage conditions.'
