param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'client\Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^(0|[1-9]\d{0,4})(\.(0|[1-9]\d{0,4})){2,3}$' -or @($Version.Split('.') | Where-Object { [int]$_ -gt 65534 }).Count -gt 0) {
    throw 'Version must contain 3 or 4 numeric components, each at most 65534.'
}
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
    --self-contained false `
    -p:Version=$Version `
    -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }

dotnet publish (Join-Path $projectRoot 'client\RemotePrint.Service\RemotePrint.Service.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained false `
    -p:Version=$Version `
    -o (Join-Path $outputDirectory 'Service')
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

# Pin the official MSI bytes as well as validating its Authenticode signature.
# The service installs it only after the server enables the tunnel.
$wireGuardArch = if ($Runtime -eq 'win-x64') { 'amd64' } else { 'arm64' }
$wireGuardHash = if ($Runtime -eq 'win-x64') {
    '7BFED60AD61B785C914B38B61555A975488E1D3EC472DBFB2FCDF498FCA75242'
} else {
    '8336335C738D4AFF040FA9D11B752DA65A80B6F41D3262D6BA5E82F97EBD1BD4'
}
$wireGuardPackage = Join-Path $outputDirectory 'WireGuardSetup.msi'
Invoke-WebRequest -Uri "https://download.wireguard.com/windows-client/wireguard-$wireGuardArch-1.1.1.msi" -OutFile $wireGuardPackage
if ((Get-FileHash -LiteralPath $wireGuardPackage -Algorithm SHA256).Hash -ne $wireGuardHash -or
    (Get-AuthenticodeSignature -LiteralPath $wireGuardPackage).Status -ne 'Valid') {
    throw 'WireGuard MSI integrity or signature verification failed.'
}

$hashes = [ordered]@{}
Get-ChildItem -LiteralPath $outputDirectory -File -Recurse | ForEach-Object {
    $hashes[$_.FullName.Substring($outputDirectory.Length).TrimStart('\')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
@{ protocol = 'native-raw-v1'; version = $Version; runtime = $Runtime; build = ('native-raw-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')); files = $hashes } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputDirectory 'build-info.json') -Encoding UTF8
$archivePath = Join-Path (Split-Path -Parent $outputDirectory) "RemotePrint.Client-$Runtime.zip"
Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $archivePath -Force
if ($Runtime -eq 'win-x64') {
    $dist = Split-Path -Parent $outputDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Force-Update-Client.cmd') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Force-Update-Client.ps1') -Destination $dist -Force
    Compress-Archive -LiteralPath (Join-Path $dist 'Force-Update-Client.cmd'), (Join-Path $dist 'Force-Update-Client.ps1'), $archivePath -DestinationPath (Join-Path $dist 'RemotePrint-Update-win-x64.zip') -Force
}
Write-Host "Published client to $outputDirectory"
Write-Host "Created archive $archivePath"
& (Join-Path $PSScriptRoot 'publish-setup.ps1')

