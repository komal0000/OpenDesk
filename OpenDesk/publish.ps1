$ErrorActionPreference = 'Stop'
$appOutput = Join-Path $PSScriptRoot 'artifacts\app'
$relayOutput = Join-Path $PSScriptRoot 'artifacts\relay'
$serviceOutput = Join-Path $PSScriptRoot 'artifacts\service'

& dotnet publish (Join-Path $PSScriptRoot 'App\OpenDesk.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $appOutput
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
& dotnet publish (Join-Path $PSScriptRoot 'Relay\OpenDesk.Relay.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $relayOutput
if ($LASTEXITCODE -ne 0) { throw 'Relay publish failed' }
& dotnet publish (Join-Path $PSScriptRoot 'Service\OpenDesk.Service.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $serviceOutput
if ($LASTEXITCODE -ne 0) { throw 'Device service publish failed' }
Copy-Item -LiteralPath (Join-Path $serviceOutput 'OpenDesk.Service.exe') -Destination (Join-Path $appOutput 'OpenDesk.Service.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $appOutput 'README.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $relayOutput 'README.md')
Compress-Archive -LiteralPath (Join-Path $appOutput 'OpenDesk.exe'), (Join-Path $appOutput 'OpenDesk.Service.exe'), (Join-Path $appOutput 'README.md') -DestinationPath (Join-Path $PSScriptRoot 'artifacts\OpenDesk-Windows-x64.zip') -Force
Compress-Archive -LiteralPath (Join-Path $relayOutput 'OpenDesk.Relay.exe'), (Join-Path $relayOutput 'README.md') -DestinationPath (Join-Path $PSScriptRoot 'artifacts\OpenDesk-Relay-Windows-x64.zip') -Force
Write-Output "App: $appOutput\OpenDesk.exe"
Write-Output "Relay: $relayOutput\OpenDesk.Relay.exe"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $serviceOutput 'README.md')
Compress-Archive -LiteralPath (Join-Path $serviceOutput 'OpenDesk.Service.exe'), (Join-Path $serviceOutput 'README.md') -DestinationPath (Join-Path $PSScriptRoot 'artifacts\OpenDesk-Service-Windows-x64.zip') -Force
Write-Output "Device service: $serviceOutput\OpenDesk.Service.exe"
