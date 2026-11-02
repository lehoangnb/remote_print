using System.Text.Json;
using RemotePrint.Client;

namespace RemotePrint.Service;

// Only completed virtual-printer output is readable through the management pipe.
// Callers supply an opaque job ID; no caller-supplied filesystem path is opened.
public sealed class TestPrintFiles(string directory)
{
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private string FilePath(string id, string extension)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Mã file in thử không hợp lệ.");
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Thư mục file in thử không hợp lệ.");
        var path = Path.Combine(directory, id + extension);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("File in thử không hợp lệ.");
        return path;
    }

    private TestPrintFile ReadInfo(string id)
    {
        var receipt = FilePath(id, ".json");
        if (new FileInfo(receipt).Length > 8192) throw new InvalidDataException("Receipt quá lớn.");
        using var json = JsonDocument.Parse(File.ReadAllText(receipt));
        var value = json.RootElement;
        var bytes = value.GetProperty("Bytes").GetInt64();
        var hash = value.GetProperty("Sha256").GetString() ?? "";
        if (value.GetProperty("Printer").GetString() != TestPrinter.Name || bytes is < 1 or > MaxFileBytes ||
            new FileInfo(FilePath(id, ".raw")).Length != bytes || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Dữ liệu file in thử không hợp lệ.");
        var (format, hasContent) = TestPrintFormat.Inspect(FilePath(id, ".raw"));
        return new(id, value.GetProperty("ReceivedAtUtc").GetDateTime(), bytes, hash, format, hasContent);
    }

    public List<TestPrintFile> List()
    {
        if (!Directory.Exists(directory)) return [];
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Thư mục file in thử không hợp lệ.");
        var result = new List<TestPrintFile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc))
        {
            try { result.Add(ReadInfo(Path.GetFileNameWithoutExtension(file.Name))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { continue; }
            if (result.Count == 200) break;
        }
        return result.OrderByDescending(file => file.ReceivedAtUtc).ToList();
    }

    public void Clear()
    {
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Thư mục file in thử không hợp lệ.");
        // No recursive deletion and no caller-supplied paths. Exclude captures
        // still being received and preserve unrelated files in the directory.
        foreach (var path in Directory.EnumerateFiles(directory).OrderByDescending(path => Path.GetExtension(path) == ".json"))
        {
            var extension = Path.GetExtension(path);
            if (extension is not (".raw" or ".json") || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) continue;
            File.Delete(FilePath(Path.GetFileNameWithoutExtension(path), extension));
        }
    }

    public async Task<TestPrintChunk> ReadAsync(string id, long offset, CancellationToken ct)
    {
        var info = ReadInfo(id);
        if (offset < 0 || offset >= info.Bytes) throw new InvalidDataException("Vị trí đọc file in thử không hợp lệ.");
        await using var stream = File.OpenRead(FilePath(id, ".raw"));
        stream.Position = offset;
        var bytes = new byte[(int)Math.Min(256 * 1024, info.Bytes - offset)];
        await stream.ReadExactlyAsync(bytes, ct);
        return new(offset, bytes, offset + bytes.Length == info.Bytes);
    }
}
