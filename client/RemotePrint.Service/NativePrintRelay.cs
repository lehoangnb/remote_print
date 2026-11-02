using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemotePrint.Client;

namespace RemotePrint.Service;

public sealed record NativeQueue(string Id, string PeerId, string Printer, string Queue, int Port);
public sealed record NativePending(string Id, string QueueId, long Bytes, string Sha256);
public sealed record NativeReceipt(string State, string Sha256, int WindowsJobId, string Printer = "");

public interface INativeRelayHost
{
    string DataDirectory { get; }
    string? NativePeerFor(string address, string localAddress);
    bool NativePrinterSelected(string printer);
    bool NativePeerAllowed(string id, string printer);
    PeerClient NativePeer(string id, string printer);
    void NativeLog(string message);
}

public sealed class NativePrintRelay(INativeRelayHost engine, Func<string, string, Action<int>, Func<bool>, int>? print = null,
    int localPort = 515, int relayPort = NativePrintProtocol.Port)
{
    private string Root => Path.Combine(engine.DataDirectory, "NativeRelay");
    private readonly SemaphoreSlim _install = new(1);
    private readonly SemaphoreSlim _spool = new(1);
    private readonly SemaphoreSlim _localCommit = new(1);
    private readonly SemaphoreSlim _slots = new(4);
    private readonly Dictionary<string, NativeQueue> _queues = new();
    private readonly List<Task> _tasks = [];
    private readonly Dictionary<string, string> _retryErrors = new();
    private TcpListener? _remote;
    public int ListenPort => ((IPEndPoint)_remote!.LocalEndpoint).Port;
    public string TestOutputDirectory => Path.Combine(Root, "TestPrints");
    public async Task ClearTestFilesAsync(CancellationToken ct)
    {
        await _localCommit.WaitAsync(ct);
        try { new TestPrintFiles(TestOutputDirectory).Clear(); engine.NativeLog("Đã dọn file in thử trên máy này."); }
        finally { _localCommit.Release(); }
    }

    public async Task PrintTestAsync(string peerId, string printer, CancellationToken ct)
    {
        // Use the connected Windows queue, so a test follows the same LPR/mesh/
        // receiving Windows spooler path as an application print job.
        if (!engine.NativePeerAllowed(peerId, printer)) throw new UnauthorizedAccessException("Máy nhận chưa online/cùng Room.");
        if (!string.Equals(printer, TestPrinter.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chọn Remote Print Test trên máy nhận để in thử.");
        NativeQueue? queue;
        lock (_queues) queue = _queues.Values.FirstOrDefault(q => q.PeerId == peerId && q.Printer == printer);
        if (queue is null) throw new InvalidOperationException("Kết nối máy in trước khi in thử.");
        await NativePrinterSetup.PowerShellAsync("""
            $q=Get-Printer -Name $a.Queue;
            if($q.DriverName -ne $a.Driver){throw 'Reconnect Remote Print Test to upgrade its Windows driver to PDF'};
            """, new { queue.Queue, Driver = TestPrinter.DriverName }, ct);
        var id = await Task.Run(() => NativeTestPage.Print(queue.Queue, peerId,
            () => !ct.IsCancellationRequested && engine.NativePeerAllowed(peerId, printer)), ct);
        engine.NativeLog($"Đã gửi trang thử vào Windows: {queue.Queue}, job {id}.");
    }

    private static void Save<T>(string path, T value)
    {
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    private bool Incoming(TcpClient client, out string peerId)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!; var local = (IPEndPoint)client.Client.LocalEndPoint!;
        peerId = engine.NativePeerFor(remote.Address.ToString(), local.Address.ToString()) ?? "";
        return peerId.Length > 0;
    }
    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        foreach (var file in Directory.GetFiles(Root, "queue-*.json"))
        {
            var queue = JsonSerializer.Deserialize<NativeQueue>(File.ReadAllText(file))!;
            lock (_queues) _queues[queue.Id] = queue;
        }
        RecoverLocalSpool();
        var capture = new LprCaptureServer(Root, ResolveLocalJobAsync, CommitLocalJobAsync, engine.NativeLog, localPort);
        var captureTask = capture.RunAsync(ct);
        if (captureTask.IsFaulted) await captureTask;
        var listener = new TcpListener(IPAddress.Any, relayPort); _remote = listener;
        listener.Start();
        var retry = RetryAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _slots.WaitAsync(ct);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch { _slots.Release(); throw; }
                Track(HandleRemoteAsync(client, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            listener.Stop(); await retry; await captureTask;
            Task[] pending; lock (_tasks) pending = _tasks.ToArray();
            await Task.WhenAll(pending);
        }
    }
    private void Track(Task task)
    {
        lock (_tasks) { _tasks.RemoveAll(t => t.IsCompleted); _tasks.Add(task); }
    }
    private async Task HandleRemoteAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10)); var token = timeout.Token;
            var scratch = Path.Combine(Root, "receive-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (!Incoming(client, out var peerId)) return;
                var stream = client.GetStream(); var request = await NativePrintProtocol.ReadAsync<NativePrintRequest>(stream, token);
                bool Authorized() => Incoming(client, out var id) && id == peerId && engine.NativePrinterSelected(request.Printer);
                if (!Authorized()) throw new UnauthorizedAccessException("Printer not shared with this Room");
                if (request.Action == "driver")
                {
                    var info = await NativePrinterSetup.ExportAsync(request.Printer, scratch, token);
                    if (!Authorized()) throw new UnauthorizedAccessException();
                    await NativePrintProtocol.WriteAsync(stream, new NativePrintReply(true, Driver: info), token);
                    await using var file = File.OpenRead(scratch);
                    var buffer = new byte[65536]; int read;
                    while ((read = await file.ReadAsync(buffer, token)) > 0)
                    { if (!Authorized()) throw new UnauthorizedAccessException(); await stream.WriteAsync(buffer.AsMemory(0, read), token); }
                }
                else if (request.Action == "print")
                {
                    if (!Guid.TryParseExact(request.JobId, "N", out _)) throw new InvalidDataException("Invalid spool ID");
                    await using (var file = File.Create(scratch))
                        await NativePrintProtocol.ReceiveAsync(stream, file, request.Bytes, request.Sha256, Authorized, token);
                    await _spool.WaitAsync(token);
                    try
                    {
                        var receipt = Path.Combine(Root, "receipt-" + peerId + "-" + request.JobId + ".json");
                        if (File.Exists(receipt))
                        {
                            var prior = JsonSerializer.Deserialize<NativeReceipt>(File.ReadAllText(receipt))!;
                            if (prior.Sha256 != request.Sha256 || prior.Printer != request.Printer || prior.State != "accepted") throw new InvalidOperationException("Kết quả spool chưa xác định; không tự gửi lại để tránh in trùng.");
                            await NativePrintProtocol.WriteAsync(stream, new NativePrintReply(true, JobId: prior.WindowsJobId), token);
                        }
                        else
                        {
                            int id;
                            if (request.Printer.Equals(TestPrinter.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!Authorized()) throw new UnauthorizedAccessException();
                                if (TestPrintFormat.Inspect(scratch).Format != "pdf")
                                    throw new InvalidDataException("Máy gửi chưa dùng driver PDF. Kết nối lại Remote Print Test và mở lại ứng dụng in.");
                                // A PDF virtual destination is already the final
                                // artifact. v4 PDF drivers do not pass incoming RAW
                                // PDF back to their port; don't spool it a second time.
                                var captureId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(peerId + ":" + request.JobId)))[..32].ToLowerInvariant();
                                await CommitTestJobCoreAsync(new(TestPrinter.QueueId, captureId), scratch, request.Bytes, token, Authorized);
                                id = 0; // Durable virtual capture has no receiving Windows spool job.
                            }
                            else
                            {
                                Save(receipt, new NativeReceipt("submitting", request.Sha256, 0, request.Printer));
                                var spool = print ?? NativeRawSpool.Print;
                                id = spool(request.Printer, scratch,
                                    job => Save(receipt, new NativeReceipt("submitting", request.Sha256, job, request.Printer)), Authorized);
                                engine.NativeLog($"Đã nhận lệnh in RAW vào Windows: {request.Printer}, job {id}.");
                            }
                            Save(receipt, new NativeReceipt("accepted", request.Sha256, id, request.Printer));
                            await NativePrintProtocol.WriteAsync(stream, new NativePrintReply(true, JobId: id), token);
                        }
                    }
                    finally { _spool.Release(); }
                }
                else throw new InvalidDataException("Unknown native relay action");
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    engine.NativeLog("Nhận relay máy in lỗi: " + ex.Message);
                    try { await NativePrintProtocol.WriteAsync(client.GetStream(), new NativePrintReply(false, ex.Message), token); } catch { }
                }
            }
            finally { if (File.Exists(scratch)) File.Delete(scratch); _slots.Release(); }
        }
    }
    public async Task InstallAsync(string peerId, string printer, CancellationToken ct)
    {
        await _install.WaitAsync(ct);
        var scratch = Path.Combine(Root, "driver-" + Guid.NewGuid().ToString("N"));
        try
        {
            var peer = engine.NativePeer(peerId, printer);
            Directory.CreateDirectory(scratch);
            var archive = Path.Combine(scratch, "package.zip");
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Parse(peer.TunnelAddress!), NativePrintProtocol.Port, ct);
            var stream = client.GetStream(); await NativePrintProtocol.WriteAsync(stream, new NativePrintRequest("driver", printer), ct);
            var reply = await NativePrintProtocol.ReadAsync<NativePrintReply>(stream, ct);
            if (!reply.Ok || reply.Driver is null) throw new InvalidOperationException(reply.Error ?? "No driver package");
            await using (var file = File.Create(archive))
                await NativePrintProtocol.ReceiveAsync(stream, file, reply.Driver.Bytes, reply.Driver.Sha256,
                    () => engine.NativePeerAllowed(peerId, printer), ct);
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(peerId + ":" + printer)))[..16];
            NativeQueue? queue; lock (_queues) _queues.TryGetValue(id, out queue);
            if (queue is null)
            {
                const int port = 515;
                var label = $"{peer.Name[..Math.Min(30, peer.Name.Length)]} {printer[..Math.Min(60, printer.Length)]}";
                label = new string(label.Select(c => c is '\\' or '/' or ':' or '"' || char.IsControl(c) ? '_' : c).ToArray());
                queue = new(id, peerId, printer, $"RemotePrint {label} [{id}]", port);
                lock (_queues) _queues[id] = queue;
                Save(Path.Combine(Root, "queue-" + id + ".json"), queue);
            }
            if (!engine.NativePeerAllowed(peerId, printer)) throw new UnauthorizedAccessException("Printer permission revoked before installation");
            await NativePrinterSetup.InstallAsync(reply.Driver, archive, Path.Combine(scratch, "files"), queue.Queue, "RP_" + id, queue.Port, ct, upgradeTest: printer == TestPrinter.Name);
            engine.NativeLog("Windows đã cài máy in bằng driver gốc: " + queue.Queue);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); _install.Release(); }
    }
    private async Task<LprTarget> ResolveLocalJobAsync(string id, CancellationToken ct)
    {
        string queueName;
        if (id == TestPrinter.QueueId) queueName = TestPrinter.Name;
        else
        {
            lock (_queues)
                queueName = _queues.TryGetValue(id, out var queue) ? queue.Queue : throw new UnauthorizedAccessException("Unknown local printer queue");
        }
        var identity = await NativePrinterSetup.PowerShellAsync("""
            $jobs=@(Get-PrintJob -PrinterName $a.Queue);
            $printing=@($jobs|Where-Object {$_.JobStatus.ToString() -match 'Printing'});
            if($printing.Count -eq 1){$job=$printing[0]}elseif($jobs.Count -eq 1){$job=$jobs[0]}else{throw 'Cannot identify the active Windows spool job'};
            if(!$job.SubmittedTime){throw 'Windows spool submission time unavailable'};
            [string]$job.ID+':'+[string]$job.SubmittedTime.ToUniversalTime().Ticks
            """, new { Queue = queueName }, ct);
        var jobId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + ":" + identity)))[..32].ToLowerInvariant();
        return new(id, jobId);
    }
    private void RecoverLocalSpool()
    {
        foreach (var path in Directory.GetFiles(Root, "local-*.json"))
        {
            var pending = JsonSerializer.Deserialize<NativePending>(File.ReadAllText(path))!;
            var manifest = Path.Combine(Root, "pending-" + pending.Id + ".json");
            if (File.Exists(Path.Combine(Root, pending.Id + ".raw")) && !File.Exists(manifest) && !File.Exists(manifest + ".review") &&
                !File.Exists(Path.Combine(Root, "delivered-" + pending.Id + ".json"))) Save(manifest, pending);
        }
    }
    private async Task CommitLocalJobAsync(LprTarget target, string scratch, long bytes, CancellationToken ct)
    {
        if (target.QueueId == TestPrinter.QueueId)
        {
            await CommitTestJobAsync(target, scratch, bytes, ct);
            return;
        }
        await _localCommit.WaitAsync(ct);
        try
        {
            string hash; await using (var file = File.OpenRead(scratch)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            var pending = new NativePending(target.JobId, target.QueueId, bytes, hash);
            var accepted = Path.Combine(Root, "local-" + target.JobId + ".json");
            if (File.Exists(accepted))
            {
                if (JsonSerializer.Deserialize<NativePending>(File.ReadAllText(accepted)) != pending) throw new InvalidDataException("Repeated Windows spool ID has different data");
                RecoverLocalSpool(); return;
            }
            var raw = Path.Combine(Root, target.JobId + ".raw");
            if (File.Exists(raw))
            {
                await using var prior = File.OpenRead(raw);
                if (new FileInfo(raw).Length != bytes || Convert.ToHexString(await SHA256.HashDataAsync(prior, ct)) != hash)
                    throw new InvalidDataException("Recovered Windows spool data differs");
            }
            else File.Move(scratch, raw);
            Save(accepted, pending);
            Save(Path.Combine(Root, "pending-" + target.JobId + ".json"), pending);
            engine.NativeLog("Đã lưu lệnh RAW từ Windows để chuyển tới máy nhận.");
        }
        finally { _localCommit.Release(); }
    }

    // Also used by the socket checks with real LPR data. No pending relay entry:
    // the virtual destination terminates the job rather than sharing it again.
    internal Task CommitTestJobAsync(LprTarget target, string scratch, long bytes, CancellationToken ct) =>
        CommitTestJobCoreAsync(target, scratch, bytes, ct, null);

    private async Task CommitTestJobCoreAsync(LprTarget target, string scratch, long bytes, CancellationToken ct, Func<bool>? authorized)
    {
        if (target.QueueId != TestPrinter.QueueId || !Guid.TryParseExact(target.JobId, "N", out _))
            throw new InvalidDataException("Invalid test spool identity");
        await _localCommit.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(TestOutputDirectory);
            var (format, hasContent) = TestPrintFormat.Inspect(scratch);
            string hash;
            await using (var file = File.OpenRead(scratch))
            {
                if (file.Length != bytes || bytes is < 1 or > NativePrintProtocol.MaxBytes) throw new InvalidDataException("Invalid test spool length");
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            }
            if (authorized is not null && !authorized()) throw new UnauthorizedAccessException("Print permission revoked before PDF capture");
            var raw = Path.Combine(TestOutputDirectory, target.JobId + ".raw");
            if (File.Exists(raw))
            {
                await using var prior = File.OpenRead(raw);
                if (prior.Length != bytes || Convert.ToHexString(await SHA256.HashDataAsync(prior, ct)) != hash)
                    throw new InvalidDataException("Repeated test spool ID has different data");
            }
            else
            {
                if (Directory.GetFiles(TestOutputDirectory, "*.raw").Sum(f => new FileInfo(f).Length) + bytes > NativePrintProtocol.MaxBytes * 4)
                    throw new IOException("Test print storage is full; remove old test files");
                File.Move(scratch, raw);
            }
            var receipt = Path.ChangeExtension(raw, ".json");
            if (File.Exists(receipt)) return;
            Save(receipt, new { Printer = TestPrinter.Name, ReceivedAtUtc = DateTime.UtcNow, Bytes = bytes, Sha256 = hash, Format = format, HasContent = hasContent });
            engine.NativeLog(hasContent ? $"Máy in ảo đã nhận: {bytes} bytes; {raw} ({format})."
                : "Máy in test nhận dữ liệu trống, không có chữ. Kết nối lại máy in PDF và mở lại ứng dụng trên máy gửi.");
        }
        finally { _localCommit.Release(); }
    }
    private async Task RetryAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var path in Directory.GetFiles(Root, "pending-*.json"))
                {
                    try
                    {
                        var pending = JsonSerializer.Deserialize<NativePending>(File.ReadAllText(path))!;
                        var delivered = Path.Combine(Root, "delivered-" + pending.Id + ".json");
                        if (File.Exists(delivered)) { File.Delete(path); if (File.Exists(Path.Combine(Root, pending.Id + ".raw"))) File.Delete(Path.Combine(Root, pending.Id + ".raw")); continue; }
                        var rawPath = Path.Combine(Root, pending.Id + ".raw");
                        if (!File.Exists(rawPath) && File.Exists(rawPath + ".part")) File.Move(rawPath + ".part", rawPath);
                        NativeQueue queue; lock (_queues) queue = _queues[pending.QueueId];
                        var peer = engine.NativePeer(queue.PeerId, queue.Printer);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(10));
                        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Parse(peer.TunnelAddress!), NativePrintProtocol.Port, timeout.Token);
                        var stream = client.GetStream();
                        await NativePrintProtocol.WriteAsync(stream, new NativePrintRequest("print", queue.Printer, pending.Id, pending.Bytes, pending.Sha256), timeout.Token);
                        await using (var file = File.OpenRead(Path.Combine(Root, pending.Id + ".raw")))
                        {
                            var buffer = new byte[65536]; int count;
                            while ((count = await file.ReadAsync(buffer, timeout.Token)) > 0)
                            {
                                if (!engine.NativePeerAllowed(queue.PeerId, queue.Printer)) throw new UnauthorizedAccessException();
                                await stream.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                            }
                        }
                        var reply = await NativePrintProtocol.ReadAsync<NativePrintReply>(stream, timeout.Token);
                        if (!reply.Ok) { engine.NativeLog("Lệnh RAW cần kiểm tra: " + reply.Error); File.Move(path, path + ".review"); continue; }
                        Save(delivered, new NativeReceipt("accepted", pending.Sha256, reply.JobId));
                        File.Delete(path); File.Delete(Path.Combine(Root, pending.Id + ".raw"));
                        _retryErrors.Remove(path);
                        engine.NativeLog(queue.Printer == TestPrinter.Name ? "Đã chuyển PDF tới máy in test trên máy nhận."
                            : $"Đã chuyển RAW vào máy nhận: {queue.Printer}, job {reply.JobId}.");
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        if (!_retryErrors.TryGetValue(path, out var prior) || prior != ex.Message)
                        { engine.NativeLog("RAW đang chờ chuyển: " + ex.Message); _retryErrors[path] = ex.Message; }
                    }
                }
                await Task.Delay(5000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

public sealed class NativePrintWorker(ClientEngine engine) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => engine.PrintRelay.RunAsync(stoppingToken);
}
