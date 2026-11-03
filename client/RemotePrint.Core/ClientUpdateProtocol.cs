using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemotePrint.Client;

public sealed record ClientRelease(string Version, string Runtime, string Notes, long Size, string Sha256, string DownloadPath,
    bool ForceUpdate = false, string ReleaseId = "");
public sealed record ClientUpdateRequest(string ServerUrl, ClientRelease Release);

public static class ClientUpdateProtocol
{
    public const string VersionHeader = "x-client-version";
    public const string RuntimeHeader = "x-client-runtime";
    public const string UpdateHeader = "x-client-update";
    public const string AppliedReleaseHeader = "x-client-update-id";
    public const long MaxPackageSize = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string CurrentVersion => typeof(ClientUpdateProtocol).Assembly.GetName().Version is { } v
        ? v.ToString(v.Revision == 0 ? 3 : 4) : "";
    public static string Runtime => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", _ => "unsupported"
    };

    private sealed record AppliedRelease(string ReleaseId, string Sha256);
    private const string ReceiptFile = "client-update-receipt.json";

    private static AppliedRelease? ReadReceipt(string? directory = null)
    {
        try
        {
            var file = Path.Combine(directory ?? AppContext.BaseDirectory, ReceiptFile);
            if (new FileInfo(file).Length > 1024) return null;
            var receipt = JsonSerializer.Deserialize<AppliedRelease>(File.ReadAllText(file), Json);
            return receipt is not null && Guid.TryParseExact(receipt.ReleaseId, "D", out _) &&
                receipt.Sha256?.Length == 64 && receipt.Sha256.All(Uri.IsHexDigit) ? receipt : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static string AppliedReleaseId => ReadReceipt()?.ReleaseId ?? "";

    // Stage the receipt beside both executables before installation. A failed install
    // keeps the old version directory and its old receipt, so the command can retry.
    public static void RecordAppliedRelease(string directory, ClientRelease release)
    {
        if (!Guid.TryParseExact(release.ReleaseId, "D", out _)) return; // Older servers have no release identity.
        File.WriteAllText(Path.Combine(directory, ReceiptFile),
            JsonSerializer.Serialize(new AppliedRelease(release.ReleaseId, release.Sha256), Json));
    }

    public static bool IsApplicableRelease(ClientRelease? release, string? installedDirectory = null)
    {
        if (release is null || release.Version is null ||
            !Regex.IsMatch(release.Version, @"^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){2,3}$") ||
            release.Version.Split('.').Any(n => int.Parse(n) > 65534) ||
            !Version.TryParse(release.Version, out var version) || release.Runtime != Runtime ||
            release.Size <= 0 || release.Size > MaxPackageSize || release.Sha256 is null ||
            release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit) ||
            release.DownloadPath != $"/api/client/updates/packages/{release.Sha256.ToLowerInvariant()}.zip" ||
            release.Notes is null || release.Notes.Length > 4000 ||
            (release.ForceUpdate && !Guid.TryParseExact(release.ReleaseId, "D", out _))) return false;
        var normalized = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        var current = typeof(ClientUpdateProtocol).Assembly.GetName().Version!;
        if (normalized != current) return normalized > current;
        if (!release.ForceUpdate) return false;
        var applied = ReadReceipt(installedDirectory);
        return applied?.ReleaseId != release.ReleaseId || !string.Equals(applied.Sha256, release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static ClientRelease? ReadUpdate(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues(UpdateHeader, out var headers)) return null;
        var values = headers.Take(2).ToArray();
        if (values.Length != 1 || values[0].Length > 2048) return null;
        try
        {
            var release = JsonSerializer.Deserialize<ClientRelease>(Convert.FromBase64String(values[0]), Json);
            return IsApplicableRelease(release) ? release : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}

// All ordinary API calls carry the build identity and can receive an update command.
internal sealed class ClientVersionHandler(HttpMessageHandler inner, Action<ClientRelease?>? updateReceived) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Remove(ClientUpdateProtocol.VersionHeader);
        request.Headers.Remove(ClientUpdateProtocol.RuntimeHeader);
        request.Headers.Remove(ClientUpdateProtocol.AppliedReleaseHeader);
        request.Headers.Add(ClientUpdateProtocol.VersionHeader, ClientUpdateProtocol.CurrentVersion);
        request.Headers.Add(ClientUpdateProtocol.RuntimeHeader, ClientUpdateProtocol.Runtime);
        if (ClientUpdateProtocol.AppliedReleaseId is { Length: > 0 } applied)
            request.Headers.Add(ClientUpdateProtocol.AppliedReleaseHeader, applied);
        var response = await base.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) updateReceived?.Invoke(ClientUpdateProtocol.ReadUpdate(response));
        return response;
    }
}
