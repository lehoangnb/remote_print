param(
    [string]$UserSid,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'RemotePrint.Client-win-x64.zip'
$tempFolder = $null
$result = $null
try {
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Missing package: $package" }
    if (-not $UserSid) { $UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
    $null = [Security.Principal.SecurityIdentifier]::new($UserSid)
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin -and -not $ValidateOnly) {
        $child = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -Wait -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-UserSid', $UserSid)
        if ($child.ExitCode -eq 0) {
            $servicePath = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\RemotePrint').ImagePath.Trim().Trim('"')
            $clientExe = Join-Path (Split-Path -Parent (Split-Path -Parent $servicePath)) 'RemotePrint.Client.exe'
            if (Test-Path -LiteralPath $clientExe -PathType Leaf) { Start-Process -FilePath $clientExe }
            else { Write-Warning "Updated client was not found at $clientExe" }
        }
        exit $child.ExitCode
    }
    if (-not $ValidateOnly -and -not (Get-Service -Name 'RemotePrint' -ErrorAction SilentlyContinue)) {
        throw 'RemotePrint service is not installed. Install it from the client UI first.'
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $tempFolder = Join-Path $tempRoot ('RemotePrint-update-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempFolder | Out-Null
    Expand-Archive -LiteralPath $package -DestinationPath $tempFolder
    $exe = Join-Path $tempFolder 'RemotePrint.Client.exe'
    $serviceExe = Join-Path $tempFolder 'Service\RemotePrint.Service.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or -not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) {
        throw 'The client package is incomplete.'
    }
    $manifest = Get-Content -LiteralPath (Join-Path $tempFolder 'build-info.json') -Raw | ConvertFrom-Json
    if ($manifest.protocol -ne 'native-raw-v1') { throw 'This package does not contain the native driver/RAW relay.' }
    foreach ($entry in $manifest.files.PSObject.Properties) {
        $file = [IO.Path]::GetFullPath((Join-Path $tempFolder $entry.Name))
        if (-not $file.StartsWith($tempFolder + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) { throw "Package hash mismatch: $($entry.Name)" }
    }
    if ($ValidateOnly) { Write-Host "Native package validated: $($manifest.build)"; exit 0 }
    $resultDir = Join-Path $env:ProgramData 'RemotePrint\SetupResults'
    $result = Join-Path $resultDir ('remoteprint-setup-' + [Guid]::NewGuid().ToString('N') + '.json')
    Write-Host 'Updating Remote Print service...'
    $installer = Start-Process -FilePath $exe -Wait -PassThru -ArgumentList @('--setup', 'InstallService', '--result', ('"' + $result + '"'), '--user', $UserSid)
    if (-not (Test-Path -LiteralPath $result -PathType Leaf)) { throw "Installer returned $($installer.ExitCode), without a result. Verify the .NET 8 Desktop Runtime and install.log." }
    $report = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if ($installer.ExitCode -ne 0 -or $report.ok -ne $true) { throw "Update failed: $($report.message) (exit $($installer.ExitCode))" }
    $service = Get-Service -Name 'RemotePrint'
    if ($service.Status -ne 'Running') { throw "Installer finished but service status is $($service.Status)." }
    $running = Get-CimInstance Win32_Service -Filter "Name='RemotePrint'"
    $installedRoot = Split-Path -Parent (Split-Path -Parent $running.PathName.Trim('"'))
    foreach ($entry in $manifest.files.PSObject.Properties) {
        $installed = [IO.Path]::GetFullPath((Join-Path $installedRoot $entry.Name))
        if (-not $installed.StartsWith($installedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $entry.Value) { throw "Installed package differs from new build: $($entry.Name)" }
    }
    Get-CimInstance Win32_Process -Filter "Name = 'RemotePrint.Client.exe'" | ForEach-Object {
        $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid -ErrorAction SilentlyContinue
        if ($owner.Sid -eq $UserSid) {
            try { Stop-Process -Id $_.ProcessId -ErrorAction Stop }
            catch { Write-Warning "Could not close old client PID $($_.ProcessId); close it from the tray." }
        }
    }
    $servicePath = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\RemotePrint').ImagePath.Trim().Trim('"')
    $clientExe = Join-Path (Split-Path -Parent (Split-Path -Parent $servicePath)) 'RemotePrint.Client.exe'
    Write-Host ('Update successful. Installed client: ' + $clientExe)
    Write-Host 'Press any key to close.'
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
} catch {
    Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
    if ($isAdmin -and -not $ValidateOnly) { Write-Host 'Press any key to close.'; $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') }
    exit 1
} finally {
    if ($result -and (Test-Path -LiteralPath $result -PathType Leaf)) { Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue }
    if ($tempFolder -and (Test-Path -LiteralPath $tempFolder)) {
        $resolved = [IO.Path]::GetFullPath($tempFolder)
        if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^RemotePrint-update-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
