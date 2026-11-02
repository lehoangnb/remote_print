using System.Text.Json;
using RemotePrint.Client;

namespace RemotePrint.Service;

public sealed class ClientEngine : IDisposable, INativeRelayHost
{
    private readonly MachineStore _store;
    private readonly WireGuardManager _tunnel;
    private readonly TunnelFirewall _tunnelFirewall;
    private readonly WindowsShareManager _windowsShares;
    private MeshRuntime? _mesh;
    private string? _nativeAddress;
    public NativePrintRelay PrintRelay { get; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ActivityEntry> _activity = [];
    private ClientConfig _config;
    private RemotePrintApi? _api;
    private ClientState _state = new();
    private List<PeerClient> _peers = [];
    private List<string> _printers = [];
    private List<string> _advertisedPrinters = [];
    private Dictionary<string, string> _windowsPrinterShares = new();
    private string _connection = "Chưa cấu hình";
    private bool _registered;
    private DateTime _lastScan;
    private DateTime _lastTunnelScan;
    private string _tunnelStatus = "Chưa cấu hình";
    private Dictionary<string, string> _tunnelPeers = new(StringComparer.OrdinalIgnoreCase);
    private ServiceSnapshot _snapshot = new();
    private string? _shareError;
    private bool _testPrinterChecked;
    public string DataDirectory => _store.DirectoryPath;

    public ClientEngine(IConfiguration configuration, IWindowsShareAccess? shareAccess = null)
    {
        _testPrinterChecked = configuration.GetValue<bool>("SkipTestPrinterUpgrade");
        _store = new MachineStore(configuration["DataDirectory"]);
        _config = _store.Load();
        _tunnel = new WireGuardManager(_store.DirectoryPath, _config.DeviceId);
        _tunnelFirewall = new TunnelFirewall(_tunnel.TunnelName);
        _config.ManagedWindowsShares = new Dictionary<string, string>(_config.ManagedWindowsShares ?? new(), StringComparer.OrdinalIgnoreCase);
        _windowsShares = new WindowsShareManager(_config, () => _store.Save(_config), shareAccess ?? new WindowsShareAccess());
        PrintRelay = new NativePrintRelay(this);
        OpenApi();
        Log("Windows Service đã khởi động. Đóng giao diện không dừng dịch vụ.");
        Publish();
    }

    public ServiceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    // Heartbeats keep authorization independent of configuration and discovery.
    public ClientConfig HeartbeatConfiguration() => new()
    {
        ServerUrl = _config.ServerUrl, DeviceId = _config.DeviceId, DeviceSecret = _config.DeviceSecret
    };

    public async Task StopTunnelAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopMeshAsync();
            _tunnelFirewall.Remove();
            await _tunnel.StopAsync(ct);
            SynchronizeWindowsShares(false);
        }
        finally { _gate.Release(); }
    }

    private void OpenApi()
    {
        _api?.Dispose();
        _api = string.IsNullOrWhiteSpace(_config.ServerUrl) ? null : new RemotePrintApi(_config);
        _registered = false;
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _lastScan > TimeSpan.FromSeconds(30))
            {
                if (!_testPrinterChecked)
                {
                    _testPrinterChecked = true;
                    try
                    {
                        if (await TestPrinterSetup.InstallAsync(ct, existingOnly: true) == "UPGRADED")
                            Log("Đã nâng cấp Remote Print Test sang PDF. Kết nối lại máy in và mở lại Word trên máy gửi.");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Log("Cập nhật máy in test PDF lỗi: " + ex.Message); }
                }
                _printers = PrinterService.GetServicePrinters().ToList();
                var shared = _config.SharedPrinters.Where(name => _printers.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                if (!shared.SequenceEqual(_advertisedPrinters, StringComparer.OrdinalIgnoreCase))
                {
                    _advertisedPrinters = shared;
                    _registered = false;
                    Log("Đã cập nhật danh sách máy in hiện khả dụng; cấu hình chia sẻ được giữ lại.");
                }
                var windowsShares = PrinterService.GetWindowsPrinterShares();
                var advertisedShares = shared.Where(windowsShares.ContainsKey)
                    .ToDictionary(name => name, name => windowsShares[name], StringComparer.Ordinal);
                if (JsonSerializer.Serialize(advertisedShares) != JsonSerializer.Serialize(_windowsPrinterShares))
                {
                    _windowsPrinterShares = advertisedShares;
                    _registered = false;
                }
                _lastScan = DateTime.UtcNow;
            }
            if (_api is null) return;
            if (!_registered)
            {
                _state = await _api.RegisterAsync(_windowsPrinterShares, _advertisedPrinters);
                if (_config.EnrollmentKey.Length > 0)
                { _config.EnrollmentKey = ""; _config.EnrollmentEndpoint = ""; _store.Save(_config); }
                _registered = true;
                Log("Đã kết nối server.");
            }
            _state = await _api.GetStatusAsync();
            _connection = _state.Status switch
            {
                "approved" => "Đã kết nối", "rejected" => "Máy bị từ chối", _ => "Chờ quản trị viên duyệt"
            };
            _peers = _state.Status == "approved"
                ? (await _api.GetPeersAsync()).Where(peer =>
                    DateTime.UtcNow - peer.LastSeenAt.ToUniversalTime() < TimeSpan.FromSeconds(30)).ToList()
                : [];
            if (DateTime.UtcNow - _lastTunnelScan >= TimeSpan.FromSeconds(15))
            {
                _lastTunnelScan = DateTime.UtcNow;
                await RefreshTunnelAsync(ct);
            }
            foreach (var peer in _peers)
            {
                var meshPeer = _mesh?.Ready.FirstOrDefault(p => p.Id == peer.Id);
                peer.TunnelAddress = _mesh is null ? _tunnelPeers.GetValueOrDefault(peer.Id) : meshPeer?.Address;
                peer.TunnelPath = meshPeer?.Path ?? "";
            }
            if (_mesh is not null) _tunnelStatus = _mesh.Status;
            Publish();

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_connection != "Mất kết nối") Log($"Kết nối lỗi: {ex.Message}");
            _connection = "Mất kết nối";
            _registered = false;
            _peers = [];
        }
        finally { Publish(); _gate.Release(); }
    }

    private async Task RefreshTunnelAsync(CancellationToken ct)
    {
        try
        {
            if (_state.Status != "approved")
            {
                await StopMeshAsync();
                _tunnelPeers.Clear();
                _tunnelFirewall.Remove();
                await _tunnel.StopAsync(ct);
                SynchronizeWindowsShares(false);
                _tunnelStatus = "Chưa được duyệt";
                return;
            }
            if (!Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out var server) ||
                (server.Scheme != Uri.UriSchemeHttps && !server.IsLoopback))
                throw new InvalidOperationException("Tunnel yêu cầu API server qua HTTPS.");
            var probe = await _api!.GetTunnelAsync(ct);
            if (!probe.Enabled)
            {
                await StopMeshAsync();
                _tunnelPeers.Clear();
                _tunnelFirewall.Remove();
                await _tunnel.StopAsync(ct);
                SynchronizeWindowsShares(false);
                _tunnelStatus = "Server chưa bật tunnel";
                return;
            }
            await _tunnel.InstallAsync(ct);
            var publicKey = await _tunnel.PublicKeyAsync(ct);
            var info = probe.Info;
            if (info is null || info.EnrolledPublicKey != publicKey)
                info = await _api.EnrollTunnelAsync(publicKey, ct);
            if (info is null)
            {
                _tunnelPeers.Clear();
                await _tunnel.StopAsync(ct);
                _tunnelStatus = "Server chưa bật tunnel";
                return;
            }
            if (info.Mode == "mesh-v1")
            {
                if (!WireGuardConfig.ValidAddress(info.Address) || info.UdpPort is < 1 or > 65535)
                    throw new InvalidDataException("Mesh configuration không hợp lệ.");
                if (_mesh is null)
                {
                    _tunnelFirewall.Remove();
                    await _tunnel.StopAsync(ct);
                    _mesh = new MeshRuntime(_config, info, _tunnel, _tunnelFirewall, () => _config.SharedPrinters.Count > 0);
                }
                _nativeAddress = info.Address;
                // Device/Room authentication replaces Windows SMB shares.
                SynchronizeWindowsShares(false);
                _tunnelStatus = _mesh.Status;
                return;
            }
            throw new InvalidOperationException("Relay driver gốc yêu cầu server TUNNEL_MODE=mesh.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            _tunnelPeers.Clear();
            var message = $"Tunnel chờ server: {ex.Message}";
            if (_tunnelStatus != message) Log(message);
            _tunnelStatus = message;
        }
        catch (Exception ex)
        {
            await StopMeshAsync();
            _tunnelPeers.Clear();
            try { _tunnelFirewall.Remove(); }
            catch (Exception firewallError) { Log($"Không xóa được quy tắc firewall tunnel: {firewallError.Message}"); }
            try { await _tunnel.StopAsync(ct); }
            catch (Exception stopError) { Log($"Không dừng được tunnel: {stopError.Message}"); }
            var message = $"Tunnel lỗi: {ex.Message}";
            if (_tunnelStatus != message) Log(message);
            _tunnelStatus = message;
        }
    }

    private void SynchronizeWindowsShares(bool enabled)
    {
        try
        {
            if (_windowsShares.Synchronize(enabled, _printers)) { _lastScan = DateTime.MinValue; _registered = false; }
            _shareError = null;
        }
        catch (Exception ex)
        {
            // A printer failure must not repeatedly tear down healthy peer tunnels.
            // Re-scan even after partial changes and retry on the next tunnel refresh.
            _lastScan = DateTime.MinValue;
            _registered = false;
            var error = ex is System.ComponentModel.Win32Exception win32
                ? $"{ex.Message} (Windows {win32.NativeErrorCode})" : ex.Message;
            if (_shareError != error) Log($"Không đồng bộ được chia sẻ máy in: {error}");
            _shareError = error;
        }
    }

    private async Task StopMeshAsync()
    {
        _nativeAddress = null;
        if (_mesh is null) return;
        var mesh = _mesh; _mesh = null;
        await mesh.DisposeAsync();
    }

    public async Task<ServiceReply> ExecuteReplyAsync(ServiceRequest request, CancellationToken ct)
    {
        var files = new TestPrintFiles(PrintRelay.TestOutputDirectory);
        return request.Action switch
        {
            "list-test-files" => new(true, TestFiles: files.List()),
            "read-test-file" => new(true, TestChunk: await files.ReadAsync(request.FileId ?? "", request.Offset, ct)),
            _ => new(true, await ExecuteAsync(request, ct))
        };
    }

    public async Task<ServiceSnapshot> ExecuteAsync(ServiceRequest request, CancellationToken ct)
    {
        if (request.Action == "status") return Snapshot;
        if (request.Action == "connect-printer")
        {
            await PrintRelay.InstallAsync(request.PeerId ?? "", request.Printer ?? "", ct);
            Publish(); return Snapshot;
        }
        if (request.Action == "print-test")
        {
            await PrintRelay.PrintTestAsync(request.PeerId ?? "", request.Printer ?? "", ct);
            Publish(); return Snapshot;
        }
        await _gate.WaitAsync(ct);
        try
        {
            switch (request.Action)
            {
                case "install-test-printer":
                    await TestPrinterSetup.InstallAsync(ct);
                    _printers = PrinterService.GetServicePrinters().ToList();
                    _lastScan = DateTime.MinValue;
                    Log("Đã cài máy in ảo Remote Print Test. Chọn máy in này để chia sẻ trong Room.");
                    break;
                case "uninstall-test-printer":
                    await TestPrinterSetup.UninstallAsync(ct);
                    _config.SharedPrinters.RemoveAll(name => name.Equals(TestPrinter.Name, StringComparison.OrdinalIgnoreCase));
                    _store.Save(_config);
                    _printers = PrinterService.GetServicePrinters().ToList();
                    _advertisedPrinters.RemoveAll(name => name.Equals(TestPrinter.Name, StringComparison.OrdinalIgnoreCase));
                    _lastScan = DateTime.MinValue;
                    _registered = false;
                    Log("Đã gỡ máy in test. File in thử được giữ lại cho tới khi dọn.");
                    break;
                case "clear-test-files":
                    await PrintRelay.ClearTestFilesAsync(ct);
                    break;
                case "configure":
                    var settings = request.Settings ?? throw new InvalidOperationException("Thiếu cấu hình.");
                    var invitation = EnrollmentCode.Parse(settings.ServerUrl.Trim());
                    var serverInput = invitation?.ServerUrl ?? settings.ServerUrl;
                    if (settings.ComputerName.Trim().Length is < 2 or > 80) throw new InvalidOperationException("Tên máy cần từ 2 đến 80 ký tự.");
                    if (!Uri.TryCreate(serverInput.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
                        || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Địa chỉ server không hợp lệ.");
                    var available = PrinterService.GetServicePrinters();
                    if (settings.SharedPrinters.Any(name => name.Equals("Remote Print", StringComparison.OrdinalIgnoreCase)
                        || (!available.Contains(name, StringComparer.OrdinalIgnoreCase)
                            && !_config.SharedPrinters.Contains(name, StringComparer.OrdinalIgnoreCase))))
                        throw new InvalidOperationException("Một máy in đã chọn không khả dụng cho Windows Service.");
                    var serverChanged = !string.Equals(_config.ServerUrl, uri.ToString().TrimEnd('/'), StringComparison.Ordinal);
                    if (serverChanged)
                    {
                        await StopMeshAsync();
                        _tunnelFirewall.Remove();
                        await _tunnel.ResetIdentityAsync(ct);
                        SynchronizeWindowsShares(false);
                    }
                    if (serverChanged)
                    {
                        _config.DeviceId = Guid.NewGuid().ToString();
                        _config.DeviceSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
                    }
                    _config.ComputerName = settings.ComputerName.Trim();
                    _config.ServerUrl = uri.ToString().TrimEnd('/');
                    if (invitation is not null || serverChanged)
                    { _config.EnrollmentEndpoint = invitation?.Endpoint ?? ""; _config.EnrollmentKey = invitation?.Key ?? ""; }
                    _config.SharedPrinters = settings.SharedPrinters.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    _lastScan = DateTime.MinValue;
                    _store.Save(_config);
                    if (serverChanged)
                    {
                        OpenApi();
                        _state = new();
                        _peers = [];
                        _connection = "Đang kết nối…";
                        _tunnelPeers.Clear();
                    }
                    else _registered = false; // Update name/shares without replacing the API or mesh session.
                    _lastTunnelScan = DateTime.MinValue;
                    Log("Đã lưu cấu hình dịch vụ.");
                    break;
                case "refresh":
                    _lastScan = DateTime.MinValue;
                    break;
                default: throw new InvalidOperationException("Thao tác không hợp lệ.");
            }
            Publish();
            return Snapshot;
        }
        finally { _gate.Release(); }
    }

    private void Publish()
    {
        var snapshot = new ServiceSnapshot
        {
            NativePrinting = true,
            TestPrinting = true,
            TestFileAccess = true,
            TestPdfPrinting = true,
            TestPrinterInstalled = _printers.Contains(TestPrinter.Name, StringComparer.OrdinalIgnoreCase),
            TestFileMaintenance = true,
            Settings = new(_config.ComputerName, _config.ServerUrl, _config.SharedPrinters.ToList()),
            State = _state, Connection = _connection, TunnelStatus = _tunnelStatus, Peers = _peers.ToList(), Printers = _printers.ToList(),
            Activity = ReadActivity(), UpdatedAt = DateTime.UtcNow
        };
        Volatile.Write(ref _snapshot, snapshot);
    }

    private List<ActivityEntry> ReadActivity()
    {
        lock (_activity) return _activity.ToList();
    }

    private void Log(string message)
    {
        lock (_activity)
        {
            _activity.Insert(0, new(DateTime.Now, message));
            if (_activity.Count > 100) _activity.RemoveAt(100);
            var logPath = Path.Combine(_store.DirectoryPath, "service.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 5_000_000) File.Move(logPath, logPath + ".previous", true);
            File.AppendAllText(logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
    }

    public void Dispose() { _api?.Dispose(); _gate.Dispose(); }

    public void NativeLog(string message) => Log(message);
    public bool NativePrinterSelected(string printer) => _config.SharedPrinters.Contains(printer, StringComparer.OrdinalIgnoreCase) &&
        _printers.Contains(printer, StringComparer.OrdinalIgnoreCase);
    public string? NativePeerFor(string address, string localAddress) => _state.Status == "approved" && _mesh?.Authorized == true &&
        localAddress == _nativeAddress ? _mesh.Ready.FirstOrDefault(p => p.Address == address)?.Id : null;
    public bool NativePeerAllowed(string id, string printer)
    {
        var peer = _peers.FirstOrDefault(p => p.Id == id);
        return _state.Status == "approved" && _mesh?.Authorized == true && peer?.NativePrinting == true &&
            peer.Printers.Contains(printer) && _mesh.Ready.Any(p => p.Id == id);
    }
    public PeerClient NativePeer(string id, string printer)
    {
        if (!NativePeerAllowed(id, printer)) throw new InvalidOperationException("Máy nhận chưa online/cùng Room hoặc chưa hỗ trợ relay driver gốc.");
        var peer = _peers.First(p => p.Id == id);
        peer.TunnelAddress = _mesh!.Ready.First(p => p.Id == id).Address;
        return peer;
    }
}
