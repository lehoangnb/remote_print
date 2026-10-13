using System.Text.Json;
using RemotePrint.Client;

namespace RemotePrint.Service;

public sealed class ClientEngine : IDisposable
{
    private readonly MachineStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _captureSync = new();
    private readonly List<ActivityEntry> _activity = [];
    private ClientConfig _config;
    private RemotePrintApi? _api;
    private ClientState _state = new();
    private List<PeerClient> _peers = [];
    private List<string> _printers = [];
    private string _connection = "Chưa cấu hình";
    private bool _registered;
    private DateTime _lastScan;
    private ServiceSnapshot _snapshot = new();
    private string PendingDirectory => Path.Combine(_store.DirectoryPath, "Pending");
    private string ReceiptDirectory => Path.Combine(_store.DirectoryPath, "Receipts");
    private readonly string _capturePath;

    public string DataDirectory => _store.DirectoryPath;

    public ClientEngine(IConfiguration configuration)
    {
        _store = new MachineStore(configuration["DataDirectory"]);
        _capturePath = Path.Combine(configuration["SpoolDirectory"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemotePrint", "Spool"), "capture.pdf");
        _config = _store.Load();
        Directory.CreateDirectory(PendingDirectory);
        Directory.CreateDirectory(ReceiptDirectory);
        OpenApi();
        Log("Windows Service đã khởi động. Đóng giao diện không dừng dịch vụ.");
        Publish();
    }

    public ServiceSnapshot Snapshot => Volatile.Read(ref _snapshot);

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
                _printers = PrinterService.GetServicePrinters().ToList();
                var shared = _config.SharedPrinters.Where(name => _printers.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                if (!shared.SequenceEqual(_config.SharedPrinters))
                {
                    _config.SharedPrinters = shared;
                    _store.Save(_config);
                    _registered = false;
                    Log("Đã cập nhật danh sách máy in dùng được trong Windows Service.");
                }
                _lastScan = DateTime.UtcNow;
            }
            if (_api is null) return;
            if (!_registered)
            {
                _state = await _api.RegisterAsync();
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
                    DateTime.UtcNow - peer.LastSeenAt.ToUniversalTime() < TimeSpan.FromSeconds(45)).ToList()
                : [];
            Publish();
            if (_state.Status == "approved")
                foreach (var job in await _api.GetPendingJobsAsync())
                {
                    ct.ThrowIfCancellationRequested();
                    await ProcessJobAsync(job);
                }
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

    public void CaptureDocument()
    {
        lock (_captureSync) CaptureDocumentCore();
    }

    private void CaptureDocumentCore()
    {
        if (!File.Exists(_capturePath)) return;
        try
        {
            using (var file = new FileStream(_capturePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (file.Length < 5) return;
                var header = new byte[5];
                file.ReadExactly(header);
                if (!header.SequenceEqual("%PDF-"u8.ToArray())) return;
            }
            File.Move(_capturePath, Path.Combine(PendingDirectory, $"{Guid.NewGuid():N}.pdf"));
            Log("Đã giữ tài liệu từ máy in ảo. Đang chờ chọn máy nhận.");
            Publish();
        }
        catch (IOException) { /* Spooler is still writing; retry on the next tick. */ }
    }

    public async Task<ServiceSnapshot> ExecuteAsync(ServiceRequest request, CancellationToken ct)
    {
        if (request.Action == "status") return Snapshot;
        await _gate.WaitAsync(ct);
        try
        {
            switch (request.Action)
            {
                case "configure":
                    var settings = request.Settings ?? throw new InvalidOperationException("Thiếu cấu hình.");
                    if (settings.ComputerName.Trim().Length is < 2 or > 80) throw new InvalidOperationException("Tên máy cần từ 2 đến 80 ký tự.");
                    if (!Uri.TryCreate(settings.ServerUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
                        || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Địa chỉ server không hợp lệ.");
                    var available = PrinterService.GetServicePrinters();
                    if (settings.SharedPrinters.Any(name => name.Equals("Remote Print", StringComparison.OrdinalIgnoreCase)
                        || !available.Contains(name, StringComparer.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Một máy in đã chọn không khả dụng cho Windows Service.");
                    _config.ComputerName = settings.ComputerName.Trim();
                    _config.ServerUrl = uri.ToString().TrimEnd('/');
                    _config.SharedPrinters = settings.SharedPrinters.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    _store.Save(_config);
                    OpenApi();
                    _state = new();
                    _peers = [];
                    _connection = "Đang kết nối…";
                    Log("Đã lưu cấu hình dịch vụ.");
                    break;
                case "refresh":
                    _lastScan = DateTime.MinValue;
                    break;
                case "cancel":
                    File.Delete(DocumentPath(request.DocumentId));
                    Log("Đã hủy tài liệu đang chờ chọn máy nhận.");
                    break;
                case "send":
                    if (_api is null || (await _api.GetStatusAsync()).Status != "approved")
                        throw new InvalidOperationException("Máy chưa được duyệt hoặc chưa kết nối.");
                    var peer = (await _api.GetPeersAsync()).FirstOrDefault(p => p.Id == request.TargetId)
                        ?? throw new InvalidOperationException("Máy nhận không còn ở cùng Room.");
                    if (DateTime.UtcNow - peer.LastSeenAt.ToUniversalTime() > TimeSpan.FromSeconds(45))
                        throw new InvalidOperationException("Máy nhận đang offline.");
                    if (request.PrinterName is null || !peer.Printers.Contains(request.PrinterName))
                        throw new InvalidOperationException("Máy in không còn được chia sẻ.");
                    var path = DocumentPath(request.DocumentId);
                    if (new FileInfo(path).Length > 25 * 1024 * 1024) throw new InvalidOperationException("Tài liệu vượt quá 25 MB.");
                    var pdf = await File.ReadAllBytesAsync(path, ct);
                    var encrypted = EncryptionService.Encrypt(pdf, peer.PublicKey);
                    await _api.CreateJobAsync(peer.Id, request.PrinterName, $"RemotePrint-{DateTime.Now:yyyyMMdd-HHmmss}.pdf", encrypted);
                    File.Delete(path);
                    Log($"Đã gửi tới {peer.Name} / {request.PrinterName}.");
                    break;
                default: throw new InvalidOperationException("Thao tác không hợp lệ.");
            }
            Publish();
            return Snapshot;
        }
        finally { _gate.Release(); }
    }

    private string DocumentPath(string? id)
    {
        if (!Guid.TryParseExact(id, "N", out var guid)) throw new InvalidOperationException("Mã tài liệu không hợp lệ.");
        return Path.Combine(PendingDirectory, $"{guid:N}.pdf");
    }

    private async Task ProcessJobAsync(PrintJob job)
    {
        if (!Guid.TryParse(job.Id, out var id)) throw new InvalidDataException("Mã lệnh in không hợp lệ.");
        var receiptPath = Path.Combine(ReceiptDirectory, $"{id:N}.json");
        var tmpPdf = Path.Combine(_store.DirectoryPath, $"printing-{id:N}.pdf");
        JobReceipt? receipt = null;
        if (File.Exists(receiptPath)) receipt = JsonSerializer.Deserialize<JobReceipt>(File.ReadAllText(receiptPath));
        if (receipt is null)
        {
            try
            {
                if (!_config.SharedPrinters.Contains(job.PrinterName))
                    throw new InvalidOperationException("Máy in này không được cho phép chia sẻ.");
                var package = await _api!.DownloadJobAsync(job.Id);
                var pdf = EncryptionService.Decrypt(package.Payload, _store.GetPrivateKey(_config));
                await File.WriteAllBytesAsync(tmpPdf, pdf);
                Log($"Đang in {job.FileName} → {job.PrinterName}");
                Publish();
                await Task.Run(() => PdfPrinter.Print(tmpPdf, job.PrinterName));
                receipt = new("completed", null);
                Log($"Windows spooler đã nhận lệnh in {job.FileName}.");
            }
            catch (Exception ex)
            {
                receipt = new("failed", ex.Message);
                Log($"In thất bại: {ex.Message}");
            }
            finally { if (File.Exists(tmpPdf)) File.Delete(tmpPdf); }
            var tmpReceipt = receiptPath + ".tmp";
            File.WriteAllText(tmpReceipt, JsonSerializer.Serialize(receipt));
            File.Move(tmpReceipt, receiptPath, true);
        }
        await _api!.UpdateJobStatusAsync(job.Id, receipt.Status, receipt.Error);
    }

    private void Publish()
    {
        var snapshot = new ServiceSnapshot
        {
            Settings = new(_config.ComputerName, _config.ServerUrl, _config.SharedPrinters.ToList()),
            State = _state, Connection = _connection, Peers = _peers.ToList(), Printers = _printers.ToList(),
            Pending = Directory.EnumerateFiles(PendingDirectory, "*.pdf").OrderBy(File.GetCreationTimeUtc)
                .Take(100).Select(file => new PendingDocument(Path.GetFileNameWithoutExtension(file), File.GetCreationTimeUtc(file))).ToList(),
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
    private sealed record JobReceipt(string Status, string? Error);
}
