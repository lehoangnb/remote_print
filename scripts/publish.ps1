param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Registry = 'registry.xn--lh-eja.vn',
    [switch]$SkipClient,
    [switch]$SkipServer,
    [switch]$IncludeLegacyRelay
)

$ErrorActionPreference = 'Stop'
if ($SkipClient -and $SkipServer) { throw 'Không thể bỏ qua cả client và server.' }
if ($Registry -notmatch '^[a-zA-Z0-9.-]+(:[0-9]{1,5})?$') {
    throw 'Registry phải là hostname hoặc hostname:port, không gồm http:// hay đường dẫn.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$image = "$Registry/remote-print-server`:latest"
$tunnelImage = "$Registry/remote-print-tunnel`:latest"

if (-not $SkipClient) {
    Write-Host "Publishing client ($Runtime)..."
    & (Join-Path $PSScriptRoot 'publish-client.ps1') -Runtime $Runtime
}

if (-not $SkipServer) {
    # Also include a fresh bootstrapper when publishing only the server.
    if ($SkipClient) { & (Join-Path $PSScriptRoot 'publish-setup.ps1') }
    $docker = Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $docker) {
        throw 'Không tìm thấy Docker CLI. Hãy cài Docker và chạy lại.'
    }
    $probe = [System.Diagnostics.Process]::new()
    $probe.StartInfo.FileName = $docker.Source
    $probe.StartInfo.Arguments = 'info --format "{{.ServerVersion}}"'
    $probe.StartInfo.UseShellExecute = $false
    $probe.StartInfo.CreateNoWindow = $true
    $probe.StartInfo.RedirectStandardOutput = $true
    $probe.StartInfo.RedirectStandardError = $true
    try {
        if (-not $probe.Start()) { throw 'Không khởi động được Docker CLI.' }
        if (-not $probe.WaitForExit(15000)) {
            $probe.Kill()
            throw 'Docker daemon chưa phản hồi sau 15 giây. Hãy đợi Docker Desktop khởi động rồi chạy lại.'
        }
        $daemonReady = $probe.ExitCode -eq 0
    }
    finally { $probe.Dispose() }
    if (-not $daemonReady) {
        throw 'Không kết nối được Docker daemon. Hãy khởi động Docker Desktop Linux engine và chạy lại.'
    }
    $serverPath = Join-Path $projectRoot 'server'
    Write-Host "Building image $image ..."
    & docker build --tag $image $serverPath
    if ($LASTEXITCODE -ne 0) { throw "Docker build thất bại (mã $LASTEXITCODE)." }
    if ($IncludeLegacyRelay) {
        Write-Host "Building legacy relay image $tunnelImage ..."
        & docker build --file (Join-Path $serverPath 'Dockerfile.tunnel') --tag $tunnelImage $serverPath
        if ($LASTEXITCODE -ne 0) { throw "Docker build relay thất bại (mã $LASTEXITCODE)." }
    }
    $registryParts = $Registry.Split(':')
    $registryPort = if ($registryParts.Length -eq 2) { [int]$registryParts[1] } else { 443 }
    if ($registryPort -lt 1 -or $registryPort -gt 65535) { throw 'Cổng registry không hợp lệ.' }
    $socket = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $socket.ConnectAsync($registryParts[0], $registryPort)
        if (-not $connect.Wait(5000) -or -not $socket.Connected) {
            throw "Không kết nối được registry $Registry. Image đã build cục bộ; hãy khởi động registry rồi chạy lại."
        }
    }
    catch {
        throw "Không kết nối được registry $Registry. Image đã build cục bộ; hãy khởi động registry rồi chạy lại."
    }
    finally { $socket.Dispose() }
    Write-Host "Pushing image $image ..."
    & docker push $image
    if ($LASTEXITCODE -ne 0) {
        throw "Docker push thất bại (mã $LASTEXITCODE). Kiểm tra registry $Registry và đăng nhập nếu cần."
    }
    if ($IncludeLegacyRelay) {
        Write-Host "Pushing legacy relay image $tunnelImage ..."
        & docker push $tunnelImage
        if ($LASTEXITCODE -ne 0) {
            throw "Docker push relay thất bại (mã $LASTEXITCODE). Kiểm tra registry $Registry và đăng nhập nếu cần."
        }
        Write-Host "Published legacy tunnel image: $tunnelImage"
    }
    Write-Host "Published server image: $image"
}
