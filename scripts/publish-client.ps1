param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'client\RemotePrint.Client\RemotePrint.Client.csproj'
$outputDirectory = Join-Path $projectRoot "dist\client-$Runtime"

if (Test-Path -LiteralPath $outputDirectory) {
    $resolvedOutput = [IO.Path]::GetFullPath($outputDirectory)
    $resolvedDist = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedOutput.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean output outside dist: $resolvedOutput"
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

dotnet publish $projectFile `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }

dotnet publish (Join-Path $projectRoot 'client\RemotePrint.Service\RemotePrint.Service.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $outputDirectory 'Service')
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

$archivePath = Join-Path (Split-Path -Parent $outputDirectory) "RemotePrint.Client-$Runtime.zip"
Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $archivePath -Force
Write-Host "Published client to $outputDirectory"
Write-Host "Created archive $archivePath"

