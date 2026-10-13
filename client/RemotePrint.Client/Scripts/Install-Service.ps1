param(
    [string]$PackageDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$UserSid = ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value),
    [string]$MigrationFile
)
$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:ProgramFiles 'RemotePrint'
$dataRoot = Join-Path $env:ProgramData 'RemotePrint'
$serviceData = Join-Path $dataRoot 'Service'
$logPath = Join-Path $dataRoot 'install.log'
try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this installer as Administrator, or use the Install service button.'
    }
    $package = (Resolve-Path -LiteralPath $PackageDirectory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $package 'Service\RemotePrint.Service.exe'))) {
        throw 'Service executable is missing. Use the complete published package.'
    }
    $sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
    New-Item -ItemType Directory -Path $installRoot,$serviceData -Force | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($allowed in @('S-1-5-18','S-1-5-32-544')) {
        $identity = [Security.Principal.SecurityIdentifier]::new($allowed)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $serviceData -AclObject $acl
    # Use a new version directory: an open UI cannot lock files being updated.
    $versionDirectory = Join-Path $installRoot ('versions\' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $package '*') -Destination $versionDirectory -Recurse -Force
    if ($MigrationFile -and -not (Test-Path -LiteralPath (Join-Path $serviceData 'config.json'))) {
        Copy-Item -LiteralPath $MigrationFile -Destination (Join-Path $serviceData 'config.json')
    }
    @{ UserSid = $sid.Value } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $serviceData 'access.json') -Encoding UTF8
    $serviceExe = Join-Path $versionDirectory 'Service\RemotePrint.Service.exe'
    $existing = Get-Service -Name 'RemotePrint' -ErrorAction SilentlyContinue
    if ($existing) {
        Stop-Service -Name 'RemotePrint' -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        & sc.exe config RemotePrint binPath= ('"' + $serviceExe + '"') start= delayed-auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot update service configuration.' }
    } else {
        New-Service -Name 'RemotePrint' -DisplayName 'Remote Print Service' -BinaryPathName ('"' + $serviceExe + '"') -StartupType Automatic -DependsOn Spooler | Out-Null
        & sc.exe config RemotePrint start= delayed-auto | Out-Null
    }
    & sc.exe description RemotePrint 'Encrypted remote printing. Runs independently of the desktop UI.' | Out-Null
    & sc.exe failure RemotePrint reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    & sc.exe failureflag RemotePrint 1 | Out-Null
    $uiExe = Join-Path $versionDirectory 'RemotePrint.Client.exe'
    $action = New-ScheduledTaskAction -Execute $uiExe -Argument '--background' -WorkingDirectory $versionDirectory
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $sid.Value
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $sid.Value -LogonType Interactive -RunLevel Limited
    $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1)) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName 'RemotePrint Desktop Agent' -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $taskSettings -Force | Out-Null
    Start-Service -Name 'RemotePrint'
    (Get-Service -Name 'RemotePrint').WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Start-ScheduledTask -TaskName 'RemotePrint Desktop Agent'
    Set-Content -LiteralPath $logPath -Value ('Installed successfully: ' + (Get-Date).ToString('O')) -Encoding UTF8
} catch {
    if (Test-Path -LiteralPath $dataRoot) { $_ | Out-String | Set-Content -LiteralPath $logPath -Encoding UTF8 }
    Write-Error $_ -ErrorAction Continue
    exit 1
}
