using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemotePrint.Client;

internal sealed class ClientUpdater : IDisposable
{
    internal const long MaxPackageSize = ClientUpdateProtocol.MaxPackageSize;
    private readonly HttpClient _http;
    internal static string CurrentVersion => ClientUpdateProtocol.CurrentVersion;
    internal static string Runtime => ClientUpdateProtocol.Runtime;

    internal ClientUpdater(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add(ClientUpdateProtocol.VersionHeader, CurrentVersion);
        _http.DefaultRequestHeaders.Add(ClientUpdateProtocol.RuntimeHeader, Runtime);
        if (ClientUpdateProtocol.AppliedReleaseId is { Length: > 0 } applied)
            _http.DefaultRequestHeaders.Add(ClientUpdateProtocol.AppliedReleaseHeader, applied);
    }

    internal static Uri ServerUri(string server)
    {
        if (!Uri.TryCreate(server.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException(L.T("Hãy cấu hình địa chỉ server HTTPS để tự động cập nhật."));
        return uri;
    }

    internal async Task<ClientRelease?> CheckAsync(string server, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(ServerUri(server), "api/client/updates/" + Runtime),
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 16384) throw new InvalidDataException(L.T("Gói cập nhật không hợp lệ."));
            buffer.Write(bytes, 0, count);
        }
        var release = JsonSerializer.Deserialize<ClientRelease>(buffer.ToArray(), ServiceProtocol.Json);
        return ClientUpdateProtocol.IsApplicableRelease(release) ? release : null;
    }

    internal async Task DownloadAsync(string server, ClientRelease release, string archive, IProgress<int>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(ServerUri(server), release.DownloadPath.TrimStart('/')), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException(L.T("Gói cập nhật không toàn vẹn."));
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > release.Size || total > MaxPackageSize) throw new InvalidDataException(L.T("Gói cập nhật không toàn vẹn."));
            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
            progress?.Report((int)(100 * total / release.Size));
        }
        if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Gói cập nhật không toàn vẹn."));
    }

    internal static void ExtractAndVerify(string archive, string target, ClientRelease release, CancellationToken ct = default)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10000) throw new InvalidDataException(L.T("Gói cập nhật không hợp lệ."));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is ".." or "." || part.EndsWith(' ') || part.EndsWith('.')) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                ((FileAttributes)entry.ExternalAttributes & FileAttributes.ReparsePoint) != 0 || !names.Add(name))
                throw new InvalidDataException(L.T("Gói cập nhật không hợp lệ."));
            var destination = Path.GetFullPath(Path.Combine(target, name));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("Gói cập nhật không hợp lệ."));
            if (name.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            total = checked(total + entry.Length);
            if (total > 1024L * 1024 * 1024) throw new InvalidDataException(L.T("Gói cập nhật không hợp lệ."));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "build-info.json")));
        var data = manifest.RootElement;
        if (data.GetProperty("version").GetString() != release.Version || data.GetProperty("runtime").GetString() != release.Runtime ||
            data.GetProperty("protocol").GetString() != "native-raw-v1") throw new InvalidDataException(L.T("Gói cập nhật không hợp lệ."));
        var files = data.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name.Replace('\\', '/'), p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { "RemotePrint.Client.exe", "RemotePrint.Client.dll", "Service/RemotePrint.Service.exe", "Service/RemotePrint.Service.dll", "translations.json" })
            if (!files.ContainsKey(required)) throw new InvalidDataException(L.T("Gói cập nhật không hợp lệ."));
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetRelativePath(target, file).Replace('\\', '/');
            if (name.Equals("build-info.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!files.TryGetValue(name, out var expected)) throw new InvalidDataException(L.T("Gói cập nhật không toàn vẹn."));
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("Gói cập nhật không toàn vẹn."));
        }
        if (files.Count != Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count() - 1)
            throw new InvalidDataException(L.T("Gói cập nhật không toàn vẹn."));
        var assembly = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(target, "RemotePrint.Client.dll")).Version!;
        if (assembly != System.Version.Parse(release.Version + (release.Version.Count(c => c == '.') == 2 ? ".0" : "")))
            throw new InvalidDataException(L.T("Gói cập nhật không hợp lệ."));
    }

    public void Dispose() => _http.Dispose();
}
