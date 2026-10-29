param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$packageRoot = Join-Path $RepositoryRoot 'installer'

function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}

function Read-ResponseLine([System.IO.Stream]$Stream) {
    $bytes = New-Object 'System.Collections.Generic.List[byte]'
    while($bytes.Count -lt 16384) {
        $n=$Stream.ReadByte()
        if($n -lt 0){throw 'Response ended inside a header'}
        if($n -eq 10){return [Text.Encoding]::ASCII.GetString($bytes.ToArray()).TrimEnd([char]13)}
        $bytes.Add([byte]$n)
    }
    throw 'Response header too large'
}

function Read-Response([System.IO.Stream]$Stream) {
    $status = Read-ResponseLine $Stream
    $headers = @{}
    while ($true) {
        $line = Read-ResponseLine $Stream
        if ($null -eq $line -or $line.Length -eq 0) { break }
        $colon = $line.IndexOf(':')
        if ($colon -gt 0) { $headers[$line.Substring(0, $colon)] = $line.Substring($colon + 1).Trim() }
    }
    $length = [int]$headers['Content-Length']
    $bytes = New-Object byte[] $length
    $read = 0
    while ($read -lt $length) {
        $n = $Stream.Read($bytes, $read, $length - $read)
        if ($n -le 0) { throw 'Response ended inside its body' }
        $read += $n
    }
    return [pscustomobject]@{ Status = $status; Body = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $read) }
}

function Send-RawRequest([int]$Port, [string]$Method, [string]$Path, [string]$Body, [string]$Token, [bool]$KeepOpen = $false) {
    $client = New-Object System.Net.Sockets.TcpClient
    $client.ReceiveTimeout = 2500
    $client.SendTimeout = 2500
    $client.Connect([System.Net.IPAddress]::Loopback, $Port)
    $stream = $client.GetStream()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $header = "$Method $Path HTTP/1.1`r`nHost: 127.0.0.1`r`nContent-Length: $($bytes.Length)`r`nConnection: keep-alive`r`n"
    if ($Token) { $header += "X-Coop-Token: $Token`r`n" }
    $header += "`r`n"
    $headBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
    $stream.Write($headBytes, 0, $headBytes.Length)
    if ($bytes.Length -gt 0) { $stream.Write($bytes, 0, $bytes.Length) }
    if (-not $KeepOpen) {
        try { return Read-Response $stream }
        finally { $client.Close() }
    }
    return [pscustomobject]@{ Client = $client; Stream = $stream }
}

$port = Get-Random -Minimum 42000 -Maximum 52000
$stdout = Join-Path $env:TEMP ('icey-web-' + [guid]::NewGuid().ToString('N') + '.out')
$stderr = Join-Path $env:TEMP ('icey-web-' + [guid]::NewGuid().ToString('N') + '.err')
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$process = $null
try {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $packageRoot 'Setup.Web.ps1'), '-Port', [string]$port, '-NoBrowser', '-PackageRoot', $packageRoot)
    $process = Start-Process -FilePath $powershell -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Assert-True ($process.Id -gt 0) 'server process did not start'
    $token = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $token) {
        if ($process.HasExited) { throw ('server exited early: ' + (Get-Content $stderr -Raw -ErrorAction SilentlyContinue)) }
        if (Test-Path -LiteralPath $stdout) {
            $text = Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue
            if ($text -match 'http://127\.0\.0\.1:(\d+)/\?t=([0-9a-f]+)') { $port = [int]$matches[1]; $token = $matches[2] }
        }
        if ($null -eq $token) { Start-Sleep -Milliseconds 100 }
    }
    Assert-True ($null -ne $token) 'server did not announce a local URL'

    $unicodePath = ([string][char]0x6E38) + [char]0x620F + [char]0x76EE + [char]0x5F55 + [char]0x4E2D + [char]0x6587
    $unicodeBody = '{"action":"NoSuchAction","path":"D:\\' + $unicodePath + '"}'
    $response = Send-RawRequest $port 'POST' '/api/action' $unicodeBody $token
    Assert-True ($response.Status -match '^HTTP/1\.1 200') ('UTF-8 action body did not complete: ' + $response.Status)
    $actionResult = $response.Body | ConvertFrom-Json
    $unknownAction = ([string][char]0x672A) + [char]0x77E5 + [char]0x64CD + [char]0x4F5C
    Assert-True ($actionResult.error -eq $unknownAction) ('UTF-8 action body was not decoded: ' + $response.Body)

    $response = Send-RawRequest $port 'POST' '/api/ack' '' ''
    Assert-True ($response.Status -match '^HTTP/1\.1 403') 'unauthenticated acknowledgement was accepted'
    $response = Send-RawRequest $port 'GET' '/api/ack' '' $token
    Assert-True ($response.Status -match '^HTTP/1\.1 405') 'GET acknowledgement was accepted'
    $response = Send-RawRequest $port 'POST' '/api/close' '' ''
    Assert-True ($response.Status -match '^HTTP/1\.1 403') 'unauthenticated close was accepted'
    $response = Send-RawRequest $port 'GET' '/api/close' '' $token
    Assert-True ($response.Status -match '^HTTP/1\.1 405') 'GET close was accepted'
    $response = Send-RawRequest $port 'GET' '/api/action' '' $token
    Assert-True ($response.Status -match '^HTTP/1\.1 405') 'GET action was accepted'
    $response = Send-RawRequest $port 'GET' '/api/state' '' $token
    Assert-True ($response.Status -match '^HTTP/1\.1 200') 'frontend state polling no longer works'

    $partialClient = New-Object System.Net.Sockets.TcpClient
    $partialClient.ReceiveTimeout = 4000
    $partialClient.SendTimeout = 2500
    $partialClient.Connect([System.Net.IPAddress]::Loopback, $port)
    $partialStream = $partialClient.GetStream()
    $header = [System.Text.Encoding]::ASCII.GetBytes("POST /api/action HTTP/1.1`r`nHost: 127.0.0.1`r`nContent-Length: 100`r`nX-Coop-Token: $token`r`n`r`n{}");
    $partialStream.Write($header, 0, $header.Length)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $partialResponse = Read-Response $partialStream
    $timer.Stop()
    $partialClient.Close()
    Assert-True ($partialResponse.Status -match '^HTTP/1\.1 408') ('partial request did not return 408: ' + $partialResponse.Status + ' ' + $partialResponse.Body)
    Assert-True ($timer.Elapsed.TotalSeconds -lt 4) 'partial request exceeded bounded timeout'

    $oversizedClient = New-Object System.Net.Sockets.TcpClient
    $oversizedClient.ReceiveTimeout = 2500
    $oversizedClient.Connect([System.Net.IPAddress]::Loopback, $port)
    $oversizedStream = $oversizedClient.GetStream()
    $oversizedHeader = [System.Text.Encoding]::ASCII.GetBytes("POST /api/action HTTP/1.1`r`nHost: 127.0.0.1`r`nContent-Length: 1048577`r`nX-Coop-Token: $token`r`n`r`n")
    $oversizedStream.Write($oversizedHeader, 0, $oversizedHeader.Length)
    $oversizedResponse = Read-Response $oversizedStream
    $oversizedClient.Close()
    Assert-True ($oversizedResponse.Status -match '^HTTP/1\.1 400') 'oversized HTTP body was not rejected'

    $response = Send-RawRequest $port 'POST' '/api/close' '' $token
    Assert-True ($response.Status -match '^HTTP/1\.1 200') 'authorized close was not accepted'
    $closeDeadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $closeDeadline) {
        $process.Refresh()
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    Assert-True ($process.HasExited) 'authorized close did not stop the idle server process'

    $fixtureRoot = Join-Path $env:TEMP ('icey-web-active-close-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageRoot 'Setup.Web.ps1') -Destination $fixtureRoot
    Copy-Item -LiteralPath (Join-Path $packageRoot 'Setup.ui.html') -Destination $fixtureRoot
    [System.IO.File]::WriteAllText((Join-Path $fixtureRoot 'manifest.json'), '{"buildId":"web-fixture-1","files":[]}', (New-Object System.Text.UTF8Encoding($false)))
    $startedMarker = Join-Path $fixtureRoot 'started.marker'
    $completedMarker = Join-Path $fixtureRoot 'completed.marker'
    $fakeCore = @'
function Read-CoopJsonFile([string]$Path, [string]$Label, [int]$Depth) { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
function Get-CoopProperty($Object, [string]$Name) { return $Object.$Name }
function Invoke-CoopAction([string]$Action, [string]$GameRoot, [string]$PackageRoot, [scriptblock]$AfterWrite) {
    [System.IO.File]::WriteAllText($env:ICEYCOOP_WEB_TEST_STARTED, 'started')
    Start-Sleep -Seconds 3
    [System.IO.File]::WriteAllText($env:ICEYCOOP_WEB_TEST_COMPLETED, 'completed')
    return [pscustomobject]@{ ok = $true; action = $Action; status = 'FixtureCompleted'; buildId = 'web-fixture-1'; gameRoot = $GameRoot; backup = ''; files = @() }
}
'@
    [System.IO.File]::WriteAllText((Join-Path $fixtureRoot 'Installer.Core.ps1'), $fakeCore, (New-Object System.Text.UTF8Encoding($false)))
    $env:ICEYCOOP_WEB_TEST_STARTED = $startedMarker
    $env:ICEYCOOP_WEB_TEST_COMPLETED = $completedMarker
    $fixturePort = Get-Random -Minimum 42000 -Maximum 52000
    $stdout = Join-Path $env:TEMP ('icey-web-fixture-' + [guid]::NewGuid().ToString('N') + '.out')
    $stderr = Join-Path $env:TEMP ('icey-web-fixture-' + [guid]::NewGuid().ToString('N') + '.err')
    $fixtureArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $fixtureRoot 'Setup.Web.ps1'), '-Port', [string]$fixturePort, '-NoBrowser', '-PackageRoot', $fixtureRoot)
    $process = Start-Process -FilePath $powershell -ArgumentList $fixtureArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Assert-True ($process.Id -gt 0) 'lifecycle fixture server did not start'
    $fixtureToken = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $fixtureToken) {
        if ($process.HasExited) { throw ('lifecycle fixture server exited early: ' + (Get-Content $stderr -Raw -ErrorAction SilentlyContinue)) }
        $text = Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue
        if ($text -match 'http://127\.0\.0\.1:(\d+)/\?t=([0-9a-f]+)') { $fixturePort = [int]$matches[1]; $fixtureToken = $matches[2] }
        if ($null -eq $fixtureToken) { Start-Sleep -Milliseconds 100 }
    }
    Assert-True ($null -ne $fixtureToken) 'lifecycle fixture server did not announce a URL'
    $targetPath = Join-Path $fixtureRoot 'fake-game'
    $actionJson = '{"action":"Install","path":"' + $targetPath.Replace('\', '\\') + '"}'
    $response = Send-RawRequest $fixturePort 'POST' '/api/action' $actionJson $fixtureToken
    Assert-True ($response.Status -match '^HTTP/1\.1 200') ('fixture action was not accepted: ' + $response.Body)

    $running = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline -and -not $running) {
        if (Test-Path -LiteralPath $startedMarker) {
            $stateResponse = Send-RawRequest $fixturePort 'GET' '/api/state' '' $fixtureToken
            if ($stateResponse.Status -match '^HTTP/1\.1 200') {
                $running = [bool](($stateResponse.Body | ConvertFrom-Json).running)
            }
        }
        if (-not $running) { Start-Sleep -Milliseconds 100 }
    }
    Assert-True ($running -and (Test-Path -LiteralPath $startedMarker)) 'fixture runspace never reached its active state'
    $response = Send-RawRequest $fixturePort 'POST' '/api/close' '' $fixtureToken
    Assert-True ($response.Status -match '^HTTP/1\.1 200') 'active close request was not accepted'
    Start-Sleep -Milliseconds 500
    $process.Refresh()
    Assert-True (-not $process.HasExited) 'server exited and interrupted the active fixture runspace'
    Assert-True (-not (Test-Path -LiteralPath $completedMarker)) 'fixture completed before the active-close assertion'

    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path -LiteralPath $completedMarker)) { Start-Sleep -Milliseconds 100 }
    Assert-True (Test-Path -LiteralPath $completedMarker) 'active fixture runspace did not finish after close was requested'
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    Assert-True ($process.HasExited) 'server did not exit naturally after the active runspace completed'
    Write-Output 'WebSetup active-close lifecycle fixture PASS (fake engine; verifies runspace lifetime only)'
    Write-Output 'WebSetup.Tests PASS'
    exit 0
} catch {
    Write-Error $_
    exit 1
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try {
            # Confirm we are stopping only the child process created above.
            $owned = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
            if ($null -ne $owned -and $owned.Path -eq $powershell -and
                $owned.StartTime.ToUniversalTime().Ticks -eq $process.StartTime.ToUniversalTime().Ticks) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    Remove-Item Env:\ICEYCOOP_WEB_TEST_STARTED -ErrorAction SilentlyContinue
    Remove-Item Env:\ICEYCOOP_WEB_TEST_COMPLETED -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    if ($null -ne $fixtureRoot -and (Test-Path -LiteralPath $fixtureRoot)) {
        $resolved=[IO.Path]::GetFullPath($fixtureRoot)
        $tempBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
        if($resolved.StartsWith($tempBase,[StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolved) -match '^icey-web-active-close-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        } else { Write-Warning 'Refusing to delete fixture outside its verified temporary directory' }
    }
}
