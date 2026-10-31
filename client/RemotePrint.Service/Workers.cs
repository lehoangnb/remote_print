using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using RemotePrint.Client;

namespace RemotePrint.Service;

public sealed class TunnelLifetime(ClientEngine engine, ILogger<TunnelLifetime> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await engine.StopTunnelAsync(cancellationToken); }
        catch (Exception ex) { logger.LogWarning("Tunnel cleanup failed: {Message}", ex.Message); }
    }
}

public sealed class HeartbeatWorker(ClientEngine engine, ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RemotePrintApi? api = null;
        string? url = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var config = engine.HeartbeatConfiguration();
                    if (config.ServerUrl != url)
                    {
                        api?.Dispose();
                        api = string.IsNullOrWhiteSpace(config.ServerUrl) ? null : new RemotePrintApi(config);
                        url = config.ServerUrl;
                    }
                    if (api is not null) await api.HeartbeatAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogDebug("Heartbeat failed: {Message}", ex.Message); }
            }
        }
        finally { api?.Dispose(); }
    }
}

public sealed class DiscoveryWorker(ClientEngine engine) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do { await engine.TickAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class PipeWorker(ClientEngine engine, ILogger<PipeWorker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settingsPath = Path.Combine(engine.DataDirectory, "access.json");
        var settings = JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(settingsPath))
            ?? throw new InvalidDataException("Thiếu tài khoản được phép quản lý service.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(settings.UserSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // The console test host can run under the configured user's identity.
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        using var slots = new SemaphoreSlim(4, 4);
        var clients = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                var pipe = NamedPipeServerStreamAcl.Create(configuration["PipeName"] ?? ServiceProtocol.PipeName, PipeDirection.InOut,
                    4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
                try { await pipe.WaitForConnectionAsync(stoppingToken); }
                catch { pipe.Dispose(); slots.Release(); throw; }
                clients.RemoveAll(task => task.IsCompleted);
                clients.Add(HandleClientAsync(pipe));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients); }

        async Task HandleClientAsync(NamedPipeServerStream pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(11));
            try
            {
                // Restrict management to the enrolled Windows account.
                string? caller = null;
                pipe.RunAsClient(() => caller = WindowsIdentity.GetCurrent().User?.Value);
                if (caller != settings.UserSid && caller != "S-1-5-18")
                    throw new UnauthorizedAccessException("Tài khoản này không được phép quản lý Remote Print.");
                var request = await ServiceProtocol.ReadAsync<ServiceRequest>(pipe, timeout.Token);
                ServiceReply reply;
                try
                {
                    reply = new(true, await engine.ExecuteAsync(request, timeout.Token));
                }
                catch (Exception ex) { reply = new(false, Error: ex.Message); }
                await ServiceProtocol.WriteAsync(pipe, reply, timeout.Token);
            }
            catch (Exception ex)
            { if (!stoppingToken.IsCancellationRequested) logger.LogWarning("IPC request failed: {Message}", ex.Message); }
            finally { await pipe.DisposeAsync(); slots.Release(); }
        }
    }
}
