param([Parameter(Mandatory = $true)][string]$SourceExecutable)
$ErrorActionPreference = 'Stop'
try {
$task = Get-ScheduledTask -TaskName 'RemotePrint Desktop Agent'
$destination = [IO.Path]::GetFullPath($task.Actions[0].Execute)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'RemotePrint\versions')) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($destination) -ne 'RemotePrint.Client.exe') {
    throw 'Unexpected installed client location.'
}
$source = (Resolve-Path -LiteralPath $SourceExecutable).Path
if ($source.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source and destination are the same.' }
$stagedPath = $destination + '.new'
Copy-Item -LiteralPath $source -Destination $stagedPath -Force
if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $stagedPath).Hash) {
    throw 'Client copy verification failed.'
}
Stop-ScheduledTask -TaskName 'RemotePrint Desktop Agent'
$sessionId = (Get-Process -Id $PID).SessionId
$clients = Get-Process -Name RemotePrint.Client -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $sessionId -and $_.Path -and
        $_.Path.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) }
foreach ($client in $clients) {
    Stop-Process -Id $client.Id -Force
    $client.WaitForExit()
}
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    try { Copy-Item -LiteralPath $stagedPath -Destination $destination -Force; break }
    catch { if ($attempt -eq 9) { throw }; Start-Sleep -Milliseconds 300 }
}
Remove-Item -LiteralPath $stagedPath
Start-ScheduledTask -TaskName 'RemotePrint Desktop Agent'
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $env:ProgramData 'RemotePrint\ui-update.log')
    Start-ScheduledTask -TaskName 'RemotePrint Desktop Agent' -ErrorAction SilentlyContinue
    exit 1
}
