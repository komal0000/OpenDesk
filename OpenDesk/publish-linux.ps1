param([ValidateSet('linux-x64')][string]$Runtime = 'linux-x64')
$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot "artifacts/service-$Runtime"
& dotnet publish (Join-Path $PSScriptRoot 'Service/OpenDesk.Service.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $output
if ($LASTEXITCODE -ne 0) { throw 'Linux service publish failed' }
$binary = [IO.File]::ReadAllBytes((Join-Path $output 'OpenDesk.Service'))
if ($binary.Length -lt 20 -or [BitConverter]::ToString($binary[0..3]) -ne '7F-45-4C-46') { throw 'Published service is not an ELF executable' }
foreach ($file in @('opendesk-server.service','install.sh','README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "deploy/linux/$file") -Destination (Join-Path $output $file)
}
$archive = Join-Path $PSScriptRoot "artifacts/OpenDesk-Service-$Runtime.tar.gz"
& tar -czf $archive -C $output OpenDesk.Service opendesk-server.service install.sh README.md
if ($LASTEXITCODE -ne 0) { throw 'Linux archive failed' }
Write-Output "Linux service bundle: $archive"
