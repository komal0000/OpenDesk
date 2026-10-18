$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'artifacts\service\OpenDesk.Service.exe'
$data = Join-Path $root ('artifacts\service-smoke-' + [guid]::NewGuid().ToString('N'))
$outLog = Join-Path $root 'artifacts\service-process-smoke.log'
$errLog = Join-Path $root 'artifacts\service-process-smoke-errors.log'
$a = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0)
$b = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0)
$a.Start(); $b.Start(); $port = $a.LocalEndpoint.Port; $relayPort = $b.LocalEndpoint.Port; $a.Stop(); $b.Stop()
$service = $null
try {
    $service = Start-Process -FilePath $exe -ArgumentList @('--data',$data,'--port',$port,'--relay-port',$relayPort,'--loopback') -WindowStyle Hidden -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    $ownedServiceHandle = $service.Handle
    $config = Join-Path $data 'service.json'
    for ($i=0; $i -lt 80 -and -not (Test-Path -LiteralPath $config); $i++) {
        if ($service.HasExited) { throw 'Packaged service exited before ready' }
        Start-Sleep -Milliseconds 150
    }
    $settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if ($settings.Pin.Length -ne 64) { throw 'Service identity fingerprint missing' }
    & $exe --stop --port $port
    if ($LASTEXITCODE -ne 0) { throw 'Packaged service stop command failed' }
    if (-not $service.WaitForExit(10000)) { throw 'Packaged service did not shut down' }
    $service.WaitForExit()
    if ($service.ExitCode -ne 0) { throw "Packaged service shutdown failed (exit $($service.ExitCode))" }
    $firstPin = $settings.Pin
    $service = Start-Process -FilePath $exe -ArgumentList @('--data',$data,'--port',$port,'--relay-port',$relayPort,'--loopback') -WindowStyle Hidden -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    $ownedServiceHandle = $service.Handle
    & $exe --stop --port $port
    if ($LASTEXITCODE -ne 0) { throw 'Restarted service stop command failed' }
    if (-not $service.WaitForExit(10000)) { throw 'Restarted service shutdown timed out' }
    $service.WaitForExit()
    if ($service.ExitCode -ne 0) { throw 'Restarted service exited with an error' }
    $reopened = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if ($reopened.Pin -ne $firstPin) { throw 'Service identity changed after restart' }
    Write-Output 'PASS packaged service: persistent identity, HTTPS configuration, current-user control pipe and graceful shutdown'
}
finally {
    if ($null -ne $service -and -not $service.HasExited) { $service.Kill(); $service.WaitForExit() }
    # Remove only the exact files created by this isolated test, including its private test certificate.
    foreach ($name in @('service-identity.pfx','service.json','service.json.tmp','devices.json','devices.json.tmp')) {
        $file = Join-Path $data $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    if ((Test-Path -LiteralPath $data) -and @(Get-ChildItem -LiteralPath $data -Force).Count -eq 0) { Remove-Item -LiteralPath $data }
}
