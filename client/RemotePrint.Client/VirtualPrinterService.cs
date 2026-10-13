using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace RemotePrint.Client;

public sealed class VirtualPrinterService
{
    public const string PrinterName = "Remote Print";
    public string SpoolDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RemotePrint", "Spool");
    public string CapturePath => Path.Combine(SpoolDirectory, "capture.pdf");

    public bool IsInstalled() => PrinterService.GetInstalledPrinters()
        .Contains(PrinterName, StringComparer.OrdinalIgnoreCase);

    public Task InstallAsync() => RunElevatedScriptAsync("Install-VirtualPrinter.ps1");
    public Task UninstallAsync() => RunElevatedScriptAsync("Uninstall-VirtualPrinter.ps1");

    private async Task RunElevatedScriptAsync(string scriptName)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Scripts", scriptName);
        if (!File.Exists(script)) throw new FileNotFoundException($"Không tìm thấy script {scriptName}.", script);

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-SpoolFile");
        start.ArgumentList.Add(CapturePath);

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Không khởi động được trình cài máy in ảo.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Cài đặt máy in ảo thất bại (mã {process.ExitCode}).");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Bạn đã hủy yêu cầu quyền quản trị nên máy in ảo chưa được cài.");
        }
    }
}

