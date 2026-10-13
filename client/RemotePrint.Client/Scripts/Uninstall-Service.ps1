$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as Administrator.' }
if (Get-ScheduledTask -TaskName 'RemotePrint Desktop Agent' -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName 'RemotePrint Desktop Agent'
    Unregister-ScheduledTask -TaskName 'RemotePrint Desktop Agent' -Confirm:$false
}
if (Get-Service -Name RemotePrint -ErrorAction SilentlyContinue) {
    Stop-Service RemotePrint -Force
    & sc.exe delete RemotePrint
}
Write-Host 'Service removed. Configuration, documents and installed virtual printer have been preserved.'
