using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace RemotePrint.Client;

public sealed record ServiceSettings(string UserSid);
public sealed record DeviceSettings(string ComputerName, string ServerUrl, List<string> SharedPrinters);
public sealed record ActivityEntry(DateTime At, string Message);
public sealed record TestPrintFile(string Id, DateTime ReceivedAtUtc, long Bytes, string Sha256, string Format = "txt", bool HasContent = true);
public sealed record TestPrintChunk(long Offset, byte[] Data, bool End);
public sealed record InstalledPrinter(string Id, string Name);
public sealed class ServiceSnapshot
{
    public ClientUpdateRequest? Update { get; set; }
    public bool NativePrinting { get; set; }
    public bool PrinterRemoval { get; set; }
    public List<InstalledPrinter> InstalledPrinters { get; set; } = [];
    public bool TestPrinting { get; set; }
    public bool TestFileAccess { get; set; }
    public bool TestPdfPrinting { get; set; }
    public bool TestPrinterInstalled { get; set; }
    public bool TestFileMaintenance { get; set; }
    public DeviceSettings Settings { get; set; } = new("", "", []);
    public ClientState State { get; set; } = new();
    public string Connection { get; set; } = "Chưa cấu hình";
    public string TunnelStatus { get; set; } = "Chưa cấu hình";
    public List<PeerClient> Peers { get; set; } = [];
    public List<string> Printers { get; set; } = [];
    public List<ActivityEntry> Activity { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}
public sealed record ServiceRequest(string Action, DeviceSettings? Settings = null, string? PeerId = null, string? Printer = null,
    string? FileId = null, long Offset = 0, string? QueueId = null);
public sealed record ServiceReply(bool Success, ServiceSnapshot? Snapshot = null, string? Error = null,
    List<TestPrintFile>? TestFiles = null, TestPrintChunk? TestChunk = null);

public static class ServiceProtocol
{
    public const string PipeName = "RemotePrint.Service.v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream stream, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Thông điệp quá lớn.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var size = BitConverter.ToInt32(header);
        if (size <= 0 || size > 1024 * 1024) throw new InvalidDataException("Thông điệp không hợp lệ.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Thông điệp rỗng.");
    }
}

public sealed class ServiceClient
{
    private readonly string _pipeName;

    public ServiceClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? ServiceProtocol.PipeName;
    }

    public async Task<ServiceSnapshot> CallAsync(ServiceRequest request)
    {
        var reply = await SendAsync(request);
        return reply.Snapshot ?? throw new InvalidDataException("Service trả về dữ liệu rỗng.");
    }

    public async Task<List<TestPrintFile>> GetTestFilesAsync(CancellationToken ct = default) =>
        (await SendAsync(new("list-test-files"), ct)).TestFiles ?? throw new InvalidDataException("Service trả về dữ liệu rỗng.");

    public async Task CopyTestFileAsync(TestPrintFile file, Stream destination, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (true)
        {
            var chunk = (await SendAsync(new("read-test-file", FileId: file.Id, Offset: offset), ct)).TestChunk
                ?? throw new InvalidDataException("Service trả về dữ liệu rỗng.");
            if (chunk.Offset != offset || chunk.Data.Length > 256 * 1024 || chunk.Data.Length == 0 || offset + chunk.Data.Length > file.Bytes)
                throw new InvalidDataException("Dữ liệu file in thử không hợp lệ.");
            await destination.WriteAsync(chunk.Data, ct);
            hash.AppendData(chunk.Data);
            offset += chunk.Data.Length;
            if (chunk.End) break;
        }
        if (offset != file.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File in thử đã thay đổi hoặc bị thiếu dữ liệu. Hãy tải lại danh sách.");
    }

    private async Task<ServiceReply> SendAsync(ServiceRequest request, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Action is "connect-printer" or "install-test-printer" or "print-test" or "clear-test-files" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(65));
        await using var pipe = new NamedPipeClientStream(".", _pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(1500, timeout.Token);
        await ServiceProtocol.WriteAsync(pipe, request, timeout.Token);
        var reply = await ServiceProtocol.ReadAsync<ServiceReply>(pipe, timeout.Token);
        if (!reply.Success) throw new InvalidOperationException(reply.Error);
        return reply;
    }
}
