using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace RemotePrint.Client;

public sealed class RemotePrintApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly ClientConfig _config;

    public RemotePrintApi(ClientConfig config, Action<ClientRelease?>? updateReceived = null)
    {
        _config = config;
        _http = new HttpClient(new ClientVersionHandler(new HttpClientHandler { AllowAutoRedirect = false }, updateReceived))
        { BaseAddress = new Uri(config.ServerUrl + "/"), Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.Add("x-device-id", config.DeviceId);
        _http.DefaultRequestHeaders.Add("x-device-secret", config.DeviceSecret);
    }

    public async Task<ClientState> RegisterAsync(Dictionary<string, string>? windowsPrinterShares = null,
        IReadOnlyList<string>? advertisedPrinters = null)
    {
        var body = new
        {
            deviceId = _config.DeviceId, secret = _config.DeviceSecret, name = _config.ComputerName,
            printers = advertisedPrinters ?? _config.SharedPrinters, windowsPrinterShares = windowsPrinterShares ?? new(), nativePrinting = true
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, string.IsNullOrEmpty(_config.EnrollmentKey) ? "api/client/register" : _config.EnrollmentEndpoint)
        { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(_config.EnrollmentKey)) request.Headers.Add("x-enrollment-key", _config.EnrollmentKey);
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && !string.IsNullOrEmpty(_config.EnrollmentKey))
        {
            // A successful enrollment response may have been lost before expiry.
            using var retry = await _http.PostAsJsonAsync("api/client/register", body);
            if (retry.IsSuccessStatusCode) return await ReadAsync<ClientState>(retry);
        }
        return await ReadAsync<ClientState>(response);
    }

    public Task<ClientState> GetStatusAsync() => GetAsync<ClientState>("api/client/status");
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/client/status", ct);
        response.EnsureSuccessStatusCode();
    }
    public Task<List<PeerClient>> GetPeersAsync() => GetAsync<List<PeerClient>>("api/client/peers");
    public async Task<(bool Enabled, TunnelInfo? Info)> GetTunnelAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/client/tunnel", ct);
        if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.NotFound)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            string? code = null;
            try
            {
                using var json = JsonDocument.Parse(content);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    code = error.GetString();
            }
            catch (JsonException) { /* Older servers may return an HTML 404. */ }
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (code == "TUNNEL_NOT_ENROLLED", null);
            if (code == "TUNNEL_NOT_CONFIGURED") return (false, null);
            throw new HttpRequestException($"Tunnel server tạm thời không khả dụng (HTTP {(int)response.StatusCode}).");
        }
        return (true, await ReadAsync<TunnelInfo>(response));
    }
    public async Task<TunnelInfo?> EnrollTunnelAsync(string publicKey, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("api/client/tunnel", new { publicKey }, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable) return null;
        return await ReadAsync<TunnelInfo>(response);
    }
    private async Task<T> GetAsync<T>(string path)
    {
        using var response = await _http.GetAsync(path);
        return await ReadAsync<T>(response);
    }
    public void Dispose() => _http.Dispose();
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions) ?? throw new InvalidOperationException("Server trả về dữ liệu rỗng.");
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var error = JsonDocument.Parse(body);
            var code = error.RootElement.GetProperty("error").GetString();
            if (code == "ENROLLMENT_REQUIRED") throw new InvalidOperationException("Máy mới cần mã ghép nối 5 phút do admin tạo. Dán mã vào ô cấu hình server.");
            if (code == "ENROLLMENT_INVALID_OR_EXPIRED") throw new InvalidOperationException("Mã ghép nối đã hết hạn hoặc đã dùng cho máy khác. Yêu cầu admin tạo mã mới.");
            throw new InvalidOperationException($"Server từ chối: {code}");
        }
        catch (JsonException) { throw new HttpRequestException($"Server trả về HTTP {(int)response.StatusCode}: {body}"); }
    }
}

