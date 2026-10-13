using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace RemotePrint.Client;

public sealed class RemotePrintApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly ClientConfig _config;

    public RemotePrintApi(ClientConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.ServerUrl + "/"), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("x-device-id", config.DeviceId);
        _http.DefaultRequestHeaders.Add("x-device-secret", config.DeviceSecret);
    }

    public async Task<ClientState> RegisterAsync()
    {
        var response = await _http.PostAsJsonAsync("api/client/register", new
        {
            deviceId = _config.DeviceId, secret = _config.DeviceSecret, name = _config.ComputerName,
            publicKey = _config.PublicKey, printers = _config.SharedPrinters
        });
        return await ReadAsync<ClientState>(response);
    }

    public Task<ClientState> GetStatusAsync() => GetAsync<ClientState>("api/client/status");
    public Task<List<PeerClient>> GetPeersAsync() => GetAsync<List<PeerClient>>("api/client/peers");
    public Task<List<PrintJob>> GetPendingJobsAsync() => GetAsync<List<PrintJob>>("api/jobs/pending");
    public Task<JobPackage> DownloadJobAsync(string id) => GetAsync<JobPackage>($"api/jobs/{Uri.EscapeDataString(id)}");

    public async Task<PrintJob> CreateJobAsync(string targetClientId, string printerName, string fileName, EncryptedPayload payload)
    {
        var response = await _http.PostAsJsonAsync("api/jobs", new { targetClientId, printerName, fileName, contentType = "application/pdf", payload });
        return await ReadAsync<PrintJob>(response);
    }

    public async Task UpdateJobStatusAsync(string id, string status, string? error)
    {
        var response = await _http.PostAsJsonAsync($"api/jobs/{Uri.EscapeDataString(id)}/status", new { status, error });
        await ReadAsync<PrintJob>(response);
    }

    private async Task<T> GetAsync<T>(string path) => await ReadAsync<T>(await _http.GetAsync(path));
    public void Dispose() => _http.Dispose();
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions) ?? throw new InvalidOperationException("Server trả về dữ liệu rỗng.");
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            var code = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString();
            throw new InvalidOperationException($"Server từ chối: {code}");
        }
        catch (JsonException) { throw new HttpRequestException($"Server trả về HTTP {(int)response.StatusCode}: {body}"); }
    }
}

