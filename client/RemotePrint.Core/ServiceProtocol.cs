using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace RemotePrint.Client;

public sealed record ServiceSettings(string UserSid);
public sealed record DeviceSettings(string ComputerName, string ServerUrl, List<string> SharedPrinters);
public sealed record PendingDocument(string Id, DateTime CreatedAt);
public sealed record ActivityEntry(DateTime At, string Message);
public sealed class ServiceSnapshot
{
    public DeviceSettings Settings { get; set; } = new("", "", []);
    public ClientState State { get; set; } = new();
    public string Connection { get; set; } = "Chưa cấu hình";
    public List<PeerClient> Peers { get; set; } = [];
    public List<string> Printers { get; set; } = [];
    public List<PendingDocument> Pending { get; set; } = [];
    public List<ActivityEntry> Activity { get; set; } = [];
    public DateTime UpdatedAt { get; set; }
}
public sealed record ServiceRequest(string Action, DeviceSettings? Settings = null,
    string? DocumentId = null, string? TargetId = null, string? PrinterName = null);
public sealed record ServiceReply(bool Success, ServiceSnapshot? Snapshot = null, string? Error = null);

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
    public async Task<ServiceSnapshot> CallAsync(ServiceRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        await using var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(1500, timeout.Token);
        await ServiceProtocol.WriteAsync(pipe, request, timeout.Token);
        var reply = await ServiceProtocol.ReadAsync<ServiceReply>(pipe, timeout.Token);
        if (!reply.Success) throw new InvalidOperationException(reply.Error);
        return reply.Snapshot ?? throw new InvalidDataException("Service trả về dữ liệu rỗng.");
    }
}
