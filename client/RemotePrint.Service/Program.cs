using RemotePrint.Client;
using RemotePrint.Service;

if (args.Length >= 4 && args[0] == "--smoke-print")
{
    PdfPrinter.Print(args[1], args[2], args[3]);
    Console.WriteLine("Print spooler accepted the PDF.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "RemotePrint");
builder.Services.AddSingleton<ClientEngine>();
builder.Services.AddHostedService<PrintWorker>();
builder.Services.AddHostedService<CaptureWorker>();
builder.Services.AddHostedService<PipeWorker>();
await builder.Build().RunAsync();
