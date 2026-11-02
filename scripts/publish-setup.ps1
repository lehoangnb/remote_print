param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $projectRoot 'dist\setup'
dotnet build (Join-Path $projectRoot 'client\RemotePrint.Setup\RemotePrint.Setup.csproj') -c Release -o $output
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
$executable = Join-Path $output 'RemotePrint.Setup.exe'
$dist = Join-Path $projectRoot 'dist\RemotePrint.Setup.exe'
$downloads = Join-Path $projectRoot 'server\public\downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination $dist -Force
Copy-Item -LiteralPath $executable -Destination (Join-Path $downloads 'RemotePrint.Setup.exe') -Force
Write-Host ('Created setup: {0} ({1:N0} bytes)' -f $dist, (Get-Item -LiteralPath $dist).Length)
