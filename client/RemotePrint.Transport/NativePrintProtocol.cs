using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemotePrint.Client;

public sealed record NativePrintRequest(string Action, string Printer, string JobId = "", long Bytes = 0, string Sha256 = "");
public sealed record NativeDriverInfo(string Name, string Environment, string InfFile, string InfSha256, long Bytes, string Sha256);
public sealed record NativePrintReply(bool Ok, string? Error = null, NativeDriverInfo? Driver = null, int JobId = 0);

public static class NativePrintProtocol
{
    public const int Port = 27444;
    public const long MaxBytes = 512L * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 65536) throw new InvalidDataException("Relay message too large");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 65536) throw new InvalidDataException("Invalid relay frame");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty relay message");
    }
    public static async Task ReceiveAsync(Stream input, Stream output, long length, string hash, Func<bool> authorized, CancellationToken ct)
    {
        if (length is < 1 or > MaxBytes || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid payload manifest");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; var remaining = length;
        while (remaining > 0)
        {
            if (!authorized()) throw new UnauthorizedAccessException("Relay permission revoked");
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (count == 0) throw new EndOfStreamException("Incomplete printer data");
            digest.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct); remaining -= count;
        }
        if (!authorized() || !Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Printer data digest or authorization mismatch");
    }
}
