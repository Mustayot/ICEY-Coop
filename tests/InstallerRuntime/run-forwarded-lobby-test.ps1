param([Parameter(Mandatory=$true)][string]$Name,[switch]$Manual,[switch]$PortableBoot,[int]$TimeoutSeconds=90)
$ErrorActionPreference='Stop'
if($Name -notmatch '^[A-Za-z0-9_-]+$'){throw 'Invalid name'}
$runtime=$PSScriptRoot
$ready=Join-Path $runtime ('evidence\'+$Name+'-forwarder-ready.txt')
if(Test-Path -LiteralPath $ready){throw 'Evidence exists'}
New-Item -ItemType Directory -Path (Split-Path $ready) -Force|Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $runtime 'build-lobby-forwarder.ps1')
if($LASTEXITCODE -ne 0){throw 'Forwarder build failed'}
$exe=Join-Path $runtime 'LobbyTcpForwarder.exe'
$proc=$null;$identity=$null;$failure=$null
try {
 $proc=Start-Process -FilePath $exe -ArgumentList @('57822','57820',$ready) -WindowStyle Hidden -PassThru
 $identity=[pscustomobject]@{Id=$proc.Id;Start=$proc.StartTime.ToUniversalTime().Ticks;Path=[IO.Path]::GetFullPath($exe)}
 $deadline=[DateTime]::UtcNow.AddSeconds(10)
 while(-not(Test-Path -LiteralPath $ready)){
  $proc.Refresh();if($proc.HasExited -or [DateTime]::UtcNow -gt $deadline){throw 'Owned TCP-only forwarder not ready'}
  Start-Sleep -Milliseconds 100
 }
 'OWNED_FORWARDER_PID='+$proc.Id
 $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $runtime 'build-run-lobby-probe.ps1'),'-EvidenceName',$Name,'-Port','57820','-Connect','127.0.0.1:57822','-TimeoutSeconds',[string]$TimeoutSeconds)
 if($Manual){$arguments+='-Manual'}
 if($PortableBoot){$arguments+='-PortableBoot'}
 & powershell @arguments
 if($LASTEXITCODE -ne 0){throw 'Native lobby run failed; retain raw reports'}
}catch{$failure=$_}finally{
 if($identity){
  $current=Get-Process -Id $identity.Id -ErrorAction SilentlyContinue
  if($current -and -not $current.HasExited){
   if($current.Path -ne $identity.Path -or $current.StartTime.ToUniversalTime().Ticks -ne $identity.Start){throw 'Forwarder identity changed; refusing to stop'}
   Stop-Process -Id $identity.Id -Force
   if(-not $current.WaitForExit(5000)){throw 'Forwarder did not exit'}
  }
 }
}
if($failure){throw $failure}
'FORWARDED_LOBBY_TEST_PASSED'
