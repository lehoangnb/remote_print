param([string]$PipeName = 'RemotePrint.Service.v1')

$ErrorActionPreference = 'Stop'
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName,
    [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous,
    [Security.Principal.TokenImpersonationLevel]::Impersonation)
try {
    $pipe.Connect(2000)
    function Read-PipeBytes([int]$Count) {
        $buffer = [byte[]]::new($Count)
        $offset = 0
        while ($offset -lt $Count) {
            $read = $pipe.ReadAsync($buffer, $offset, $Count - $offset)
            if (!$read.Wait(5000)) { throw 'Timed out reading the service reply.' }
            $received = $read.GetAwaiter().GetResult()
            if ($received -eq 0) { throw 'Incomplete service reply.' }
            $offset += $received
        }
        return ,$buffer
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes('{"action":"status"}')
    $pipe.Write([BitConverter]::GetBytes($bytes.Length), 0, 4)
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
    $header = Read-PipeBytes 4
    $length = [BitConverter]::ToInt32($header, 0)
    if ($length -lt 1 -or $length -gt 1MB) { throw 'Invalid service reply size.' }
    $data = Read-PipeBytes $length
    if ($data.Length -ne $length) { throw 'Incomplete service reply.' }
    $reply = [Text.Encoding]::UTF8.GetString($data) | ConvertFrom-Json
    if (!$reply.success) { throw $reply.error }
    $snapshot = $reply.snapshot
    [pscustomobject]@{
        at = $snapshot.updatedAt
        connection = $snapshot.connection
        tunnel = $snapshot.tunnelStatus
        mesh = $snapshot.meshDiagnostics
        peers = @($snapshot.peers | Select-Object id, name, tunnelAddress, tunnelPath)
    } | ConvertTo-Json -Depth 9
} finally {
    $pipe.Dispose()
}
