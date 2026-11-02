using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemotePrint.Client;

namespace RemotePrint.Service;

public static class NativePrinterSetup
{
    private static string DriverEnvironment => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "Windows ARM64" : "Windows x64";
    public static async Task<string> PowerShellAsync(string script, object arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["REMOTEPRINT_ARGUMENTS"] = JsonSerializer.Serialize(arguments);
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $a=$env:REMOTEPRINT_ARGUMENTS|ConvertFrom-Json; " + script)) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot run printer setup");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var result = await output; var failure = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows printer setup: " + failure);
        return result.Trim().Trim('\uFEFF');
    }

    public static async Task<NativeDriverInfo> ExportAsync(string printer, string archive, CancellationToken ct)
    {
        var json = await PowerShellAsync("""
            $p=Get-Printer | Where-Object Name -eq $a.Printer | Select-Object -First 1;
            if(!$p){throw 'Printer not found'};
            if($p.PortName -like 'RP_*'){throw 'A relayed printer cannot be exported again'};
            $d=Get-PrinterDriver | Where-Object { $_.Name -eq $p.DriverName -and $_.PrinterEnvironment -eq $a.Environment } | Select-Object -First 1;
            if(!$d -or !$d.InfPath){throw 'Driver package INF unavailable'};
            @{Name=$d.Name; Environment=$d.PrinterEnvironment; Inf=$d.InfPath}|ConvertTo-Json -Compress
            """, new { Printer = printer, Environment = DriverEnvironment }, ct);
        using var info = JsonDocument.Parse(json); var root = info.RootElement;
        var inf = Path.GetFullPath(root.GetProperty("Inf").GetString()!);
        var store = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository")) + Path.DirectorySeparatorChar;
        if (!inf.StartsWith(store, StringComparison.OrdinalIgnoreCase) || !File.Exists(inf)) throw new InvalidDataException("Driver INF outside Windows driver store");
        var folder = Path.GetDirectoryName(inf)!;
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        if (files.Length > 10000 || files.Any(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) || files.Sum(f => new FileInfo(f).Length) > NativePrintProtocol.MaxBytes)
            throw new InvalidDataException("Driver package exceeds limits");
        ZipFile.CreateFromDirectory(folder, archive, CompressionLevel.Fastest, false);
        await using var zipStream = File.OpenRead(archive);
        return new(root.GetProperty("Name").GetString()!, root.GetProperty("Environment").GetString()!, Path.GetFileName(inf),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(inf, ct))), new FileInfo(archive).Length,
            Convert.ToHexString(await SHA256.HashDataAsync(zipStream, ct)));
    }

    public static void ExtractChecked(string archive, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        long bytes = 0;
        if (zip.Entries.Count > 10000) throw new InvalidDataException("Too many driver files");
        foreach (var entry in zip.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':') || (bytes += entry.Length) > NativePrintProtocol.MaxBytes)
                throw new InvalidDataException("Unsafe driver archive");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, false);
        }
    }

    public static async Task InstallAsync(NativeDriverInfo driver, string archive, string folder, string queue, string portName, int port, CancellationToken ct, bool upgradeTest = false)
    {
        var expected = DriverEnvironment;
        if (driver.Environment != expected || Path.GetFileName(driver.InfFile) != driver.InfFile || !driver.InfFile.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Gói driver khác kiến trúc Windows hoặc không có INF hợp lệ.");
        ExtractChecked(archive, folder);
        var inf = Path.Combine(folder, driver.InfFile);
        if (!File.Exists(inf) || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(inf, ct))) != driver.InfSha256)
            throw new InvalidDataException("Driver INF digest mismatch");
        // Inbox drivers cannot be staged as OEM packages with pnputil. For the
        // PDF test printer use Windows' own signed inbox driver on each client.
        var inboxPdf = driver.Name == TestPrinter.DriverName && driver.InfFile.Equals("prnms009.inf", StringComparison.OrdinalIgnoreCase);
        if (inboxPdf) await PowerShellAsync(TestPrinterSetup.EnsureDriverScript, new { }, ct);
        await PowerShellAsync("""
            $existing=Get-PrinterDriver | Where-Object { $_.Name -eq $a.Driver -and $_.PrinterEnvironment -eq $a.Environment } | Select-Object -First 1;
            if(!$a.InboxPdf -and (!$existing -or !(Test-Path -LiteralPath $existing.InfPath) -or (Get-FileHash -LiteralPath $existing.InfPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $a.Inf -Algorithm SHA256).Hash)){
              & "$env:windir\System32\pnputil.exe" /add-driver $a.Inf | Out-Null;
              if($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010){throw 'Windows rejected driver package signature/staging'};
              $hash=(Get-FileHash -LiteralPath $a.Inf -Algorithm SHA256).Hash;
              $base=[IO.Path]::GetFileNameWithoutExtension($a.Inf);
              $store=Get-ChildItem -LiteralPath "$env:windir\System32\DriverStore\FileRepository" -Directory |
                Where-Object { $_.Name.StartsWith($base+'_',[StringComparison]::OrdinalIgnoreCase) } |
                ForEach-Object { Join-Path $_.FullName ([IO.Path]::GetFileName($a.Inf)) } |
                Where-Object { (Test-Path -LiteralPath $_) -and (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash -eq $hash } | Select-Object -First 1;
              if(!$store){throw 'Staged driver INF not found'};
              Add-PrinterDriver -Name $a.Driver -InfPath $store;
            };
            $p=Get-CimInstance Win32_TCPIPPrinterPort | Where-Object Name -eq $a.PortName | Select-Object -First 1;
            if($p -and ($p.HostAddress -ne '127.0.0.1' -or $p.Protocol -ne 2 -or $p.Queue -ne $a.QueueId)){throw 'Port name belongs to another configuration'};
            if(!$p){Add-PrinterPort -Name $a.PortName -LprHostAddress '127.0.0.1' -LprQueueName $a.QueueId -LprByteCounting};
            $q=Get-Printer | Where-Object Name -eq $a.Queue | Select-Object -First 1;
            if($q -and $q.PortName -eq $a.PortName -and $q.DriverName -eq $a.Legacy -and $a.UpgradeTest){
              if(@(Get-PrintJob -PrinterName $a.Queue).Count -gt 0){throw 'Finish or cancel pending test jobs before reconnecting the PDF test printer'};
              Set-Printer -Name $a.Queue -DriverName $a.Driver;
              $q=Get-Printer -Name $a.Queue;
            };
            if($q -and ($q.PortName -ne $a.PortName -or $q.DriverName -ne $a.Driver)){throw 'Queue belongs to another configuration'};
            if(!$q){Add-Printer -Name $a.Queue -DriverName $a.Driver -PortName $a.PortName -PrintProcessor 'winprint' -Datatype 'RAW' -Comment 'RemotePrint native relay'};
            'OK'
            """, new { Driver = driver.Name, driver.Environment, Inf = inf, Queue = queue, PortName = portName, QueueId = portName[3..], InboxPdf = inboxPdf,
                UpgradeTest = upgradeTest && inboxPdf, Legacy = TestPrinter.LegacyDriverName }, ct);
    }
}
