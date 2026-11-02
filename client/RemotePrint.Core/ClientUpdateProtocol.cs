using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemotePrint.Client;

public sealed record ClientRelease(string Version, string Runtime, string Notes, long Size, string Sha256, string DownloadPath);
public sealed record ClientUpdateRequest(string ServerUrl, ClientRelease Release);

public static class ClientUpdateProtocol
{
    public const string VersionHeader = "x-client-version";
    public const string RuntimeHeader = "x-client-runtime";
    public const string UpdateHeader = "x-client-update";
    public const long MaxPackageSize = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string CurrentVersion => typeof(ClientUpdateProtocol).Assembly.GetName().Version is { } v
        ? v.ToString(v.Revision == 0 ? 3 : 4) : "";
    public static string Runtime => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", _ => "unsupported"
    };

    public static bool IsNewerRelease(ClientRelease? release)
    {
        if (release is null || release.Version is null ||
            !Regex.IsMatch(release.Version, @"^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){2,3}$") ||
            release.Version.Split('.').Any(n => int.Parse(n) > 65534) ||
            !Version.TryParse(release.Version, out var version) || release.Runtime != Runtime ||
            release.Size <= 0 || release.Size > MaxPackageSize || release.Sha256 is null ||
            release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit) ||
            release.DownloadPath != $"/api/client/updates/packages/{release.Sha256.ToLowerInvariant()}.zip" ||
            release.Notes is null || release.Notes.Length > 4000) return false;
        var normalized = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        return normalized > typeof(ClientUpdateProtocol).Assembly.GetName().Version!;
    }

    public static ClientRelease? ReadUpdate(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues(UpdateHeader, out var headers)) return null;
        var values = headers.Take(2).ToArray();
        if (values.Length != 1 || values[0].Length > 2048) return null;
        try
        {
            var release = JsonSerializer.Deserialize<ClientRelease>(Convert.FromBase64String(values[0]), Json);
            return IsNewerRelease(release) ? release : null;
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
        request.Headers.Add(ClientUpdateProtocol.VersionHeader, ClientUpdateProtocol.CurrentVersion);
        request.Headers.Add(ClientUpdateProtocol.RuntimeHeader, ClientUpdateProtocol.Runtime);
        var response = await base.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) updateReceived?.Invoke(ClientUpdateProtocol.ReadUpdate(response));
        return response;
    }
}
