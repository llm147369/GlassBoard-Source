# Isolated native supplement. No network, player files or account access.
$bridgeRoot=[Environment]::GetEnvironmentVariable('GLASSBOARD_HOME')
if(-not $bridgeRoot){$bridgeRoot=Split-Path $PSScriptRoot -Parent}
$ErrorActionPreference='Stop'
$pipeInput=New-Object IO.StreamReader([Console]::OpenStandardInput(),(New-Object Text.UTF8Encoding($false,$true)))
$pipeOutput=New-Object IO.StreamWriter([Console]::OpenStandardOutput(),(New-Object Text.UTF8Encoding($false)))
$pipeOutput.AutoFlush=$true
[Console]::SetIn($pipeInput)
[Console]::SetOut($pipeOutput)
$npsm=$null;$startupError=$null;$lastInitialize=[DateTime]::MinValue
try{Add-Type -Path (Join-Path $bridgeRoot 'lib\NPSMLib.dll');$npsm=New-Object NPSMLib.NowPlayingSessionManager}catch{$startupError=$_.Exception.GetBaseException().Message}
function Close-Safely($value) { try { if ($null -ne $value -and $value -is [IDisposable]) { ([IDisposable]$value).Dispose() } } catch { } }
function Get-TimelineSafely($session) {
    try { return $session.GetTimelineProperties() } catch {
        return [pscustomobject]@{ StartTime=[TimeSpan]::Zero; EndTime=[TimeSpan]::Zero; Position=[TimeSpan]::Zero; MinSeekTime=[TimeSpan]::Zero; MaxSeekTime=[TimeSpan]::Zero; LastUpdatedTime=[DateTimeOffset]::UtcNow; Error=$_.Exception.GetBaseException().Message }
    }
}
function Same-MediaText($left,$right) { return $left -and $right -and ([string]$left).Trim().Normalize().Equals(([string]$right).Trim().Normalize(),[StringComparison]::OrdinalIgnoreCase) }
function Find-NativeMedia($source, $title, $artist) {
    $script:nativeObserved=New-Object Collections.Generic.List[string]
    if ($null -eq $npsm) { return $null }
    try {
        foreach ($candidate in $npsm.GetSessions()) {
            try {
                $data = $candidate.ActivateMediaPlaybackDataSource()
                $info = $data.GetMediaObjectInfo()
                $app="";try{$app = [string]$data.GetParentApplicationId()}catch{}
                if($script:nativeObserved.Count -lt 4){$script:nativeObserved.Add($app+" / "+[string]$info.Title)}
                $sameSource = $source -and $app -and $app.Equals($source, [StringComparison]::OrdinalIgnoreCase)
                if (-not $sameSource -and $source) {
                    try { $proc = [Diagnostics.Process]::GetProcessById([int]$candidate.PID);try { $name=$proc.ProcessName; $sameSource=$name.Length -ge 3 -and $source.IndexOf($name,[StringComparison]::OrdinalIgnoreCase) -ge 0 } finally { $proc.Dispose() } } catch {}
                }
                $sameSong = Same-MediaText $title $info.Title
                if ($title -and $info.Title -and -not $sameSong) { continue }
                if ($sameSource -or ($sameSong -and $artist -and (Same-MediaText $info.Artist $artist))) { return $data }
            } catch { }
        }
    } catch { }
    return $null
}

while($null -ne ($line=[Console]::ReadLine())){
 try{
  $request=$line|ConvertFrom-Json
  if($request.Command -eq 'exit'){break}
  if($startupError -and ([DateTime]::UtcNow-$lastInitialize).TotalSeconds -ge 5){$lastInitialize=[DateTime]::UtcNow;try{$npsm=New-Object NPSMLib.NowPlayingSessionManager;$startupError=$null}catch{$startupError=$_.Exception.GetBaseException().Message}}
  if($startupError){throw $startupError}
  $direct=$request.Command -eq 'discover' -or ([string]$request.Key).StartsWith('native|')
  if($direct){
   $selected=$null;$best=-1
   foreach($candidate in $npsm.GetSessions()) {try{
    $data=$candidate.ActivateMediaPlaybackDataSource();$info=$data.GetMediaObjectInfo();if(-not $info.Title){continue}
    $source=[string]$data.GetParentApplicationId();if(-not $source){$source='pid:'+([string]$candidate.PID)}
    $identity='native|'+$source+'|'+[string]$info.Title+'|'+[string]$info.Artist+'|'+[string]$info.AlbumTitle
    if($request.Command -ne 'discover' -and $identity -ne [string]$request.Key){continue}
    $state=$data.GetMediaPlaybackInfo();$score=1;if([string]$state.PlaybackState -eq 'Playing'){$score+=100}
    if($source -match '(?i)cloudmusic|qqmusic|spotify|musicbee|foobar|aimp'){$score+=10}
    if($score -gt $best){$best=$score;$selected=@{Data=$data;Info=$info;Source=$source;Key=$identity;Playback=$state}}
   }catch{}}
   if($null -eq $selected){throw '未找到可读取的播放器会话；请确认播放器已开始播放并公开媒体信息'}
   $native=$selected.Data;$info=$selected.Info;$state=$selected.Playback;$status='OK'
   switch($request.Command){
    'toggle' {$toggle=[NPSMLib.MediaPlaybackCommands]::PlayPauseToggle;if(([int]$state.PlaybackCaps -band 1024) -eq 0){$toggle=if([string]$state.PlaybackState -eq 'Playing'){[NPSMLib.MediaPlaybackCommands]::Pause}else{[NPSMLib.MediaPlaybackCommands]::Play}};if(-not $native.SendMediaPlaybackCommand($toggle)){$status='Player did not accept this action'}}
    'previous' {if(-not $native.SendMediaPlaybackCommand([NPSMLib.MediaPlaybackCommands]::Previous)){$status='Player did not accept this action'}}
    'next' {if(-not $native.SendMediaPlaybackCommand([NPSMLib.MediaPlaybackCommands]::Next)){$status='Player did not accept this action'}}
   }
   try{$state=$native.GetMediaPlaybackInfo()}catch{}
   $cover='';$coverStatus='Not supplied';$stream=$null;$memory=$null
   try{$stream=$native.GetThumbnailStream();if($stream -and $stream.Length -gt 0 -and $stream.Length -le 8388608){$memory=New-Object IO.MemoryStream;$stream.CopyTo($memory);$cover=[Convert]::ToBase64String($memory.ToArray());$coverStatus='NPSMLib thumbnail'}}catch{$coverStatus=$_.Exception.GetBaseException().Message}finally{Close-Safely $memory;Close-Safely $stream}
   $reply=@{Available=$true;Status=$status;Source=$selected.Source;Key=$selected.Key;Title=[string]$info.Title;Artist=[string]$info.Artist;Album=[string]$info.AlbumTitle;Playing=([string]$state.PlaybackState -eq 'Playing');Cover=$cover;CoverStatus=$coverStatus;MetadataStatus='OK';Backend='NPSMLib direct';CanToggle=(([int]$state.PlaybackCaps -band 1027) -ne 0);CanPrevious=(([int]$state.PlaybackCaps -band 128) -ne 0);CanNext=(([int]$state.PlaybackCaps -band 64) -ne 0)}
   [Console]::WriteLine(($reply|ConvertTo-Json -Compress -Depth 4));[Console]::Out.Flush();continue
  }
  $native=Find-NativeMedia ([string]$request.Source) ([string]$request.Title) ([string]$request.Artist)
  if($null -eq $native){throw ('No matching native session; observed: '+($script:nativeObserved -join '; '))}
  $info=$native.GetMediaObjectInfo()
  $expected=[string]$request.Source+'|'+[string]$request.Title+'|'+[string]$request.Artist+'|'+[string]$request.Album
  if(-not $request.Source -or -not $request.Key -or [string]$request.Key -ne $expected){throw 'Session identity mismatch'}
  if($request.Title -and $info.Title -and -not (Same-MediaText $request.Title $info.Title)){throw 'Track changed'}
  $duration=0.0;$position=0.0;$minimum=0.0;$maximum=0.0;$age=0.0;$rate=1.0;$canSeek=$false;$timelineStatus='Not supplied'
  if(-not $request.CoverOnly){try{
   $timeline=$native.GetMediaTimelineProperties();$playback=$native.GetMediaPlaybackInfo()
   $duration=[Math]::Max(0,($timeline.EndTime-$timeline.StartTime).TotalSeconds)
   $position=[Math]::Max(0,($timeline.Position-$timeline.StartTime).TotalSeconds)
   $minimum=[Math]::Max(0,($timeline.MinSeekTime-$timeline.StartTime).TotalSeconds)
   $maximum=[Math]::Min($duration,[Math]::Max(0,($timeline.MaxSeekTime-$timeline.StartTime).TotalSeconds));if($maximum -le $minimum){$maximum=$duration}
   $canSeek=([int]$playback.PlaybackCaps -band [int][NPSMLib.MediaPlaybackCapabilities]::PlaybackPosition) -ne 0 -and $duration -gt 0
   if($playback.PlaybackRate -gt 0){$rate=[double]$playback.PlaybackRate}
   try{if($timeline.PositionSetFileTime.Year -gt 2000){$age=[Math]::Max(0,[Math]::Min(86400,([DateTime]::UtcNow-$timeline.PositionSetFileTime.ToUniversalTime()).TotalSeconds))}}catch{}
   $timelineStatus=if($duration -gt 0){'OK'}else{'Not supplied'}
  }catch{$timelineStatus=$_.Exception.GetBaseException().Message}}
  $status='OK'
  if($request.Command -eq 'seek'){
   if(-not $canSeek){$status='Player did not accept this action'}else{
    $seconds=[Math]::Max($minimum,[Math]::Min($maximum,[double]$request.Seconds))
    if($native.SendPlaybackPositionChangeRequest([TimeSpan]::FromTicks($timeline.StartTime.Ticks+[long]($seconds*10000000)))){$position=$seconds;$age=0.0}else{$status='Player did not accept this action'}
   }
  }
  $cover='';$coverStatus='Not supplied';$stream=$null;$memory=$null
  if($request.Command -ne 'seek'){
   try{$stream=$native.GetThumbnailStream();if($stream -and $stream.Length -gt 0 -and $stream.Length -le 8388608){$memory=New-Object IO.MemoryStream;$stream.CopyTo($memory);$cover=[Convert]::ToBase64String($memory.ToArray());$coverStatus='NPSMLib thumbnail'}}catch{$coverStatus=$_.Exception.GetBaseException().Message}finally{Close-Safely $memory;Close-Safely $stream}
  }
  $reply=@{Available=$true;Status=$status;Key=[string]$request.Key;Source=[string]$request.Source;Title=[string]$info.Title;Artist=[string]$info.Artist;Album=[string]$info.AlbumTitle;Duration=$duration;Position=$position;MinSeek=$minimum;MaxSeek=$maximum;Age=$age;Rate=$rate;CanSeek=$canSeek;Cover=$cover;CoverStatus=$coverStatus;TimelineStatus=$timelineStatus;Backend='NPSMLib isolated'}
 }catch{$reply=@{Available=$false;Status=$_.Exception.GetBaseException().Message;Key=[string]$request.Key;Cover=''}}
 [Console]::WriteLine(($reply|ConvertTo-Json -Compress -Depth 4));[Console]::Out.Flush()
}
