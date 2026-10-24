# GlassBoard media-session bridge. No network or account access.
param([switch]$NoNative)
$bridgeRoot=[Environment]::GetEnvironmentVariable('GLASSBOARD_HOME')
if(-not $bridgeRoot){$bridgeRoot=Split-Path $PSScriptRoot -Parent}
$ErrorActionPreference = 'Stop'
$pipeInput=New-Object IO.StreamReader([Console]::OpenStandardInput(),(New-Object Text.UTF8Encoding($false,$true)))
$pipeOutput=New-Object IO.StreamWriter([Console]::OpenStandardOutput(),(New-Object Text.UTF8Encoding($false)))
$pipeOutput.AutoFlush=$true
[Console]::SetIn($pipeInput)
[Console]::SetOut($pipeOutput)
$ProgressPreference = 'SilentlyContinue'
$manager = $null
$startupError = $null
$lastCoverKey = ''
$cover = ''
$lastCoverAttempt = [DateTime]::MinValue
$coverStatus = 'Not supplied'
$npsm = $null
$nativeAttempted = $false
function Close-Safely($value) { try { if ($null -ne $value -and $value -is [IDisposable]) { ([IDisposable]$value).Dispose() } } catch { } }
function Get-TimelineSafely($session,$coverOnly=$false) {
    if($coverOnly){return [pscustomobject]@{StartTime=[TimeSpan]::Zero;EndTime=[TimeSpan]::Zero;Position=[TimeSpan]::Zero;MinSeekTime=[TimeSpan]::Zero;MaxSeekTime=[TimeSpan]::Zero;LastUpdatedTime=[DateTimeOffset]::UtcNow}}
    try { return $session.GetTimelineProperties() } catch {
        return [pscustomobject]@{ StartTime=[TimeSpan]::Zero; EndTime=[TimeSpan]::Zero; Position=[TimeSpan]::Zero; MinSeekTime=[TimeSpan]::Zero; MaxSeekTime=[TimeSpan]::Zero; LastUpdatedTime=[DateTimeOffset]::UtcNow; Error=$_.Exception.GetBaseException().Message }
    }
}
function Find-NativeMedia($source, $title, $artist) {
    if ($null -eq $npsm) { return $null }
    try {
        foreach ($candidate in $npsm.GetSessions()) {
            try {
                $data = $candidate.ActivateMediaPlaybackDataSource()
                $info = $data.GetMediaObjectInfo()
                $app = [string]$data.GetParentApplicationId()
                $sameSource = $source -and $app -and $app.Equals($source, [StringComparison]::OrdinalIgnoreCase)
                if (-not $sameSource -and $source) {
                    $proc = [Diagnostics.Process]::GetProcessById([int]$candidate.PID)
                    try { $name=$proc.ProcessName; $sameSource=$name.Length -ge 3 -and $source.IndexOf($name,[StringComparison]::OrdinalIgnoreCase) -ge 0 } finally { $proc.Dispose() }
                }
                $sameSong = $title -and $info.Title -and [string]$info.Title -eq [string]$title
                if ($title -and $info.Title -and -not $sameSong) { continue }
                if ($sameSource -or ($sameSong -and $artist -and [string]$info.Artist -eq [string]$artist)) { return $data }
            } catch { }
        }
    } catch { }
    return $null
}
function Initialize-MediaManager {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $managerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]
    $script:propertiesType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType=WindowsRuntime]
    $script:streamType = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime]
    $script:asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    function script:Await($operation, $resultType) {
        $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
        if (-not $task.Wait(4000)) { throw 'Media operation timed out' }
        return $task.Result
    }
    # 精简版/非零售版 Windows 上 RequestAsync 可能返回 null（媒体组件缺失）：
    # 重试三次仍为空则抛出明确错误，而不是让会话静默为空。
    $script:manager = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $op = $managerType::RequestAsync()
            if ($null -ne $op) { $script:manager = Await $op $managerType }
        } catch { }
        if ($null -ne $script:manager) { break }
        Start-Sleep -Milliseconds 1200
    }
    if ($null -eq $script:manager) { throw 'System media session manager unavailable (RequestAsync returned null). If this is a reduced/N edition of Windows, install the Media Feature Pack.' }
}
try { Initialize-MediaManager } catch { $startupError = $_.Exception.GetBaseException().Message }
$lastInitialize=[DateTime]::UtcNow
while ($null -ne ($line = [Console]::ReadLine())) {
    try {
        $request = $line | ConvertFrom-Json
        if ($request.Command -eq 'exit') { break }
        if ($startupError -and ([DateTime]::UtcNow-$lastInitialize).TotalSeconds -ge 5) {
            $lastInitialize=[DateTime]::UtcNow
            try { Initialize-MediaManager; $startupError=$null } catch { $startupError=$_.Exception.GetBaseException().Message }
        }
        if ($startupError) { throw $startupError }
        # 只识别音乐类会话：黑名单制（排除浏览器/视频站），其余播放器默认放行
        $blockPattern = '(?i)(msedge|chrome|firefox|opera|brave|qqbrowser|360se|360chrome|sogou|iexplore|maxthon|thorium|vivaldi|lbbrowser|2345explorer|bilibili|douyin|iqiyi|youku|tenvideo|qqlive|mgtv|tudou|youtube|netflix|primevideo)'
        if ($request.Command -eq 'diagnose') {
            $sb = New-Object Text.StringBuilder
            $diagInfo = 'ok'
            try {
                if ($null -eq $manager) { $diagInfo = 'manager-null' }
                $allSessions = $manager.GetSessions()
                $diagInfo = 'count=' + $allSessions.Count
                foreach ($candidate in $allSessions) {
                    try {
                        $pbx = $candidate.GetPlaybackInfo()
                        $px = $null
                        try { $px = Await ($candidate.TryGetMediaPropertiesAsync()) $propertiesType } catch { }
                        $null = $sb.AppendLine([string]$candidate.SourceAppUserModelId + ' | ' + [string]$pbx.PlaybackType + ' | ' + [string]$pbx.PlaybackStatus + ' | ' + [string]$px.Title + ' | ' + [string]$px.Artist)
                    } catch { }
                }
            } catch { $diagInfo = 'error: ' + $_.Exception.GetBaseException().Message }
            if ($sb.Length -eq 0) { $null = $sb.AppendLine('(no sessions)') }
            $head = '[session-enum ' + $diagInfo + ' | OS ' + [Environment]::OSVersion.VersionString + ' | PS ' + $PSVersionTable.PSVersion.ToString() + ']'
            $reply = @{Available=$false; Status='diagnose'; Cover=''; Value=($head + "`n" + $sb.ToString().TrimEnd())}
            [Console]::WriteLine(($reply | ConvertTo-Json -Compress -Depth 4))
            [Console]::Out.Flush()
            continue
        }
        $session=$null
        try {
            $current = $manager.GetCurrentSession()
            if ($null -ne $current) {
                $pb0 = $current.GetPlaybackInfo()
                $t0 = [string]$pb0.PlaybackType
                if (($t0 -eq 'Music' -or $t0 -eq 'Unknown' -or $t0 -eq '') -and [string]$current.SourceAppUserModelId -notmatch $blockPattern) { $session = $current }
            }
        } catch { }
        if ($null -eq $session) {
            try {
                foreach ($candidate in $manager.GetSessions()) {
                    try {
                        $pb = $candidate.GetPlaybackInfo()
                        $pbType = [string]$pb.PlaybackType
                        if ($pbType -eq 'Video' -or $pbType -eq 'Image') { continue }
                        if ([string]$candidate.SourceAppUserModelId -match $blockPattern) { continue }
                        if ([string]$pb.PlaybackStatus -eq 'Playing') { $session = $candidate; break }
                        if ($null -eq $session) { $session = $candidate }
                    } catch { }
                }
            } catch { }
        }
        if ($null -ne $session) {
            try {
                $selProps = Await ($session.TryGetMediaPropertiesAsync()) $script:propertiesType
                if ([string]$selProps.Title -match '(?i)\.(mp4|mkv|avi|flv|mov|wmv|m4v|webm|ts|mpg|mpeg|rm|rmvb|3gp|vob|ogv|m2ts)(\W|$)') { $session = $null }
            } catch { }
        }
        if ($null -eq $session) {
            $lastCoverKey = ''; $cover = ''
            $reply = @{Available=$false; Status='Start playback in your music app'; Cover=''}
        } else {
            $result = $true
            $properties = $null
            $metadataStatus='OK'
            try { $properties = Await ($session.TryGetMediaPropertiesAsync()) $propertiesType } catch { $metadataStatus=$_.Exception.GetBaseException().Message }
            $key = [string]$session.SourceAppUserModelId + '|' + [string]$properties.Title + '|' + [string]$properties.Artist + '|' + [string]$properties.AlbumTitle
            $playback = $session.GetPlaybackInfo()
            $timeline = Get-TimelineSafely $session ([bool]$request.CoverOnly)
            $duration = [Math]::Max(0, ($timeline.EndTime-$timeline.StartTime).TotalSeconds)
            $minimum = [Math]::Max(0, ($timeline.MinSeekTime-$timeline.StartTime).TotalSeconds)
            $maximum = [Math]::Min($duration, [Math]::Max(0, ($timeline.MaxSeekTime-$timeline.StartTime).TotalSeconds))
            if ($maximum -le $minimum) { $maximum=$duration }
            $rate = 1.0
            if ($null -ne $playback.PlaybackRate) { $rate = [double]$playback.PlaybackRate }
            $age = 0.0
            try { if(([DateTimeOffset]$timeline.LastUpdatedTime).Year -gt 2000){$age=[Math]::Max(0, [Math]::Min(86400, ([DateTimeOffset]::UtcNow - [DateTimeOffset]$timeline.LastUpdatedTime).TotalSeconds))} } catch { }
            $native = $null
            $backend = 'GSMTC'
            if ($request.UseNative -and -not $NoNative -and ($duration -le 0 -or -not $playback.Controls.IsPlaybackPositionEnabled -or $request.Command -eq 'cover' -or $request.Command -eq 'seek' -or -not $cover)) {
                if(-not $nativeAttempted){$nativeAttempted=$true;try{Add-Type -Path (Join-Path $bridgeRoot 'lib\NPSMLib.dll');$npsm=New-Object NPSMLib.NowPlayingSessionManager}catch{}}
                $native = Find-NativeMedia ([string]$session.SourceAppUserModelId) ([string]$properties.Title) ([string]$properties.Artist)
            }
            $nativeSeek = $false
            if ($null -ne $native) {
                try {
                    $nativeTimeline=$native.GetMediaTimelineProperties(); $nativePlayback=$native.GetMediaPlaybackInfo()
                    $nativeDuration=[Math]::Max(0,($nativeTimeline.EndTime-$nativeTimeline.StartTime).TotalSeconds)
                    $nativeSeek=([int]$nativePlayback.PlaybackCaps -band [int][NPSMLib.MediaPlaybackCapabilities]::PlaybackPosition) -ne 0 -and $nativeDuration -gt 0
                    if ($nativeDuration -gt 0 -and ($duration -le 0 -or (-not $playback.Controls.IsPlaybackPositionEnabled -and $nativeSeek))) {
                        $backend='NPSMLib'; $timeline=$nativeTimeline; $duration=$nativeDuration
                        $minimum=[Math]::Max(0,($timeline.MinSeekTime-$timeline.StartTime).TotalSeconds)
                        $maximum=[Math]::Min($duration,[Math]::Max(0,($timeline.MaxSeekTime-$timeline.StartTime).TotalSeconds))
                        if($maximum -le $minimum){$maximum=$duration}
                        $age=0.0;try{if($timeline.PositionSetFileTime.Year -gt 2000){$age=[Math]::Max(0,[Math]::Min(86400,([DateTime]::UtcNow-$timeline.PositionSetFileTime.ToUniversalTime()).TotalSeconds))}}catch{}
                    }
                } catch { $nativeSeek=$false }
            }
            switch ($request.Command) {
                'toggle' { $result = Await ($session.TryTogglePlayPauseAsync()) ([bool]) }
                'previous' { $result = Await ($session.TrySkipPreviousAsync()) ([bool]) }
                'next' { $result = Await ($session.TrySkipNextAsync()) ([bool]) }
                'seek' {
                    if ($request.Key -and [string]$request.Key -ne $key) { $result=$false }
                    elseif ($duration -le 0 -or (-not $playback.Controls.IsPlaybackPositionEnabled -and -not $nativeSeek)) { $result=$false }
                    else {
                        $seconds = [Math]::Max($minimum, [Math]::Min($maximum, [double]$request.Seconds))
                        $ticks = $timeline.StartTime.Ticks + [long]($seconds * 10000000)
                        if ($backend -eq 'NPSMLib') { $result=$native.SendPlaybackPositionChangeRequest([TimeSpan]::FromTicks($ticks)) }
                        else {
                            try { $result = Await ($session.TryChangePlaybackPositionAsync($ticks)) ([bool]) } catch { $result=$false }
                            if (-not $result -and $nativeSeek) { $result=$native.SendPlaybackPositionChangeRequest([TimeSpan]::FromTicks($ticks)) }
                        }
                    }
                }
            }
            if ($request.Command -eq 'toggle' -or $request.Command -eq 'previous' -or $request.Command -eq 'next') {
                $native=$null;$nativeSeek=$false;$backend='GSMTC'
                $playback=$session.GetPlaybackInfo();$timeline=Get-TimelineSafely $session ([bool]$request.CoverOnly)
                try{$properties=Await ($session.TryGetMediaPropertiesAsync()) $propertiesType;$metadataStatus='OK'}catch{$metadataStatus=$_.Exception.GetBaseException().Message}
                $key=[string]$session.SourceAppUserModelId+'|'+[string]$properties.Title+'|'+[string]$properties.Artist+'|'+[string]$properties.AlbumTitle
                $duration=[Math]::Max(0,($timeline.EndTime-$timeline.StartTime).TotalSeconds)
                $minimum=[Math]::Max(0,($timeline.MinSeekTime-$timeline.StartTime).TotalSeconds)
                $maximum=[Math]::Min($duration,[Math]::Max(0,($timeline.MaxSeekTime-$timeline.StartTime).TotalSeconds))
                if($maximum -le $minimum){$maximum=$duration}
                $age=0.0;try{$age=[Math]::Max(0,[Math]::Min(86400,([DateTimeOffset]::UtcNow-[DateTimeOffset]$timeline.LastUpdatedTime).TotalSeconds))}catch{}
            }
            $position=[Math]::Max(0,($timeline.Position-$timeline.StartTime).TotalSeconds)
            if ($request.Command -eq 'seek' -and $result) { $position=$seconds; $age=0.0 }
            $newCover = ''
            if ($lastCoverKey -ne $key) { $cover=''; $lastCoverAttempt=[DateTime]::MinValue; $coverStatus='Not supplied'; $lastCoverKey=$key }
            # 只在显式请求封面时才读取缩略图；refresh 走轻量路径，保证播放状态秒级同步
            if ($request.Command -eq 'cover') {
                $lastCoverAttempt=[DateTime]::UtcNow
                $bytes=$null
                if ($null -ne $properties.Thumbnail) {
                    $winStream=$null; $reader=$null
                    try {
                        $winStream=Await ($properties.Thumbnail.OpenReadAsync()) $streamType
                        if ($winStream.Size -gt 0 -and $winStream.Size -le 8388608) {
                            $readerType=[Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType=WindowsRuntime]
                            $reader=$readerType::new($winStream.GetInputStreamAt(0))
                            $loaded=Await ($reader.LoadAsync([uint32]$winStream.Size)) ([uint32])
                            [byte[]]$bytes=New-Object byte[] ([int]$loaded)
                            $reader.ReadBytes($bytes)
                            $coverStatus='GSMTC thumbnail'
                        }
                    } catch { $coverStatus='GSMTC thumbnail: '+$_.Exception.GetBaseException().Message } finally {
                        Close-Safely $reader; Close-Safely $winStream
                    }
                }
                if (($null -eq $bytes -or $bytes.Length -eq 0) -and $null -ne $native) {
                    $nativeStream=$null; $memory=$null
                    try {
                        $nativeStream=$native.GetThumbnailStream()
                        if($nativeStream -and $nativeStream.Length -gt 0 -and $nativeStream.Length -le 8388608){
                            $memory=New-Object IO.MemoryStream; $nativeStream.CopyTo($memory); $bytes=$memory.ToArray(); $coverStatus='NPSMLib thumbnail'
                        }
                    } catch { $coverStatus='NPSMLib thumbnail: '+$_.Exception.GetBaseException().Message } finally {
                        Close-Safely $memory;Close-Safely $nativeStream
                    }
                }
                if($null -ne $bytes -and $bytes.Length -gt 0){$nextCover=[Convert]::ToBase64String($bytes);if($nextCover -ne $cover -or $request.Command -eq 'cover'){$cover=$nextCover;$newCover=$cover}}
            }
            if($request.Command -eq 'cover' -and -not $newCover){$newCover=$cover}
            $reply = @{Available=$true; Status=($(if($result){'OK'}else{'Player did not accept this action'})); Title=[string]$properties.Title; Artist=[string]$properties.Artist; Album=[string]$properties.AlbumTitle; Source=[string]$session.SourceAppUserModelId; Key=$key; Cover=$newCover; Playing=([string]$playback.PlaybackStatus -eq 'Playing'); Position=$position; Duration=$duration; Age=$age; Rate=$rate; MinSeek=$minimum; MaxSeek=$maximum; Backend=$backend; MetadataStatus=$metadataStatus; TimelineStatus=($(if($timeline.Error){[string]$timeline.Error}else{'OK'})); CoverStatus=$coverStatus; CanSeek=((([bool]$playback.Controls.IsPlaybackPositionEnabled) -or $nativeSeek) -and $duration -gt 0); CanPrevious=[bool]$playback.Controls.IsPreviousEnabled; CanNext=[bool]$playback.Controls.IsNextEnabled; CanToggle=([bool]$playback.Controls.IsPlayEnabled -or [bool]$playback.Controls.IsPauseEnabled)}
        }
    } catch { $reply = @{Available=$false; Status=$_.Exception.GetBaseException().Message; Cover=''} }
    [Console]::WriteLine(($reply | ConvertTo-Json -Compress -Depth 4))
    [Console]::Out.Flush()
}
