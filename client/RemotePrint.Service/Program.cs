using RemotePrint.Client;
using RemotePrint.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "RemotePrint");
builder.Services.AddSingleton<ClientEngine>();
builder.Services.AddHostedService<TunnelLifetime>();
builder.Services.AddHostedService<HeartbeatWorker>();
builder.Services.AddHostedService<DiscoveryWorker>();
builder.Services.AddHostedService<PipeWorker>();
builder.Services.AddHostedService<NativePrintWorker>();
await builder.Build().RunAsync();
