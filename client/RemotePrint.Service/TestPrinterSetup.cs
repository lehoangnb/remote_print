using RemotePrint.Client;

namespace RemotePrint.Service;

public static class TestPrinterSetup
{
    public static Task<string> UninstallAsync(CancellationToken ct) => NativePrinterSetup.PowerShellAsync("""
        $q=Get-Printer | Where-Object Name -eq $a.Name | Select-Object -First 1;
        if($q){
          if($q.PortName -ne $a.Port -or $q.DriverName -notin @($a.Driver,$a.Legacy)){throw 'Test printer name belongs to another configuration'};
          if(@(Get-PrintJob -PrinterName $a.Name).Count -gt 0){throw 'Finish or cancel pending test jobs before removing the test printer'};
          Remove-Printer -Name $a.Name;
        };
        $p=Get-CimInstance Win32_TCPIPPrinterPort | Where-Object Name -eq $a.Port | Select-Object -First 1;
        if($p -and $p.HostAddress -eq '127.0.0.1' -and $p.Protocol -eq 2 -and $p.Queue -eq $a.Queue -and !(Get-Printer | Where-Object PortName -eq $a.Port)){
          Remove-PrinterPort -Name $a.Port;
        };
        'OK'
        """, new { Name = TestPrinter.Name, Port = TestPrinter.PortName, Driver = TestPrinter.DriverName, Legacy = TestPrinter.LegacyDriverName, Queue = TestPrinter.QueueId }, ct);

    internal const string EnsureDriverScript = """
        if(!(Get-PrinterDriver | Where-Object Name -eq 'Microsoft Print To PDF')){
          $arch=if([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -eq 'Arm64'){'arm64'}else{'amd64'};
          $inf=Get-ChildItem -LiteralPath "$env:windir\System32\DriverStore\FileRepository" -Directory -Filter ('prnms009.inf_'+$arch+'_*') |
            ForEach-Object {Join-Path $_.FullName 'prnms009.inf'} | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1;
          if(!$inf){throw 'Windows Microsoft Print to PDF is unavailable. Enable Microsoft Print to PDF in Windows Features first.'};
          Add-PrinterDriver -Name 'Microsoft Print To PDF' -InfPath $inf;
        };
        """;

    public static Task<string> InstallAsync(CancellationToken ct, bool existingOnly = false) => NativePrinterSetup.PowerShellAsync("""
        $q=Get-Printer | Where-Object Name -eq $a.Name | Select-Object -First 1;
        if($a.ExistingOnly -and !$q){return 'ABSENT'};
        if($q -and ($q.PortName -ne $a.Port -or $q.DriverName -notin @($a.Driver,$a.Legacy))){throw 'Test printer name belongs to another configuration'};
        if($q -and $q.DriverName -eq $a.Legacy -and @(Get-PrintJob -PrinterName $a.Name).Count -gt 0){throw 'Finish or cancel pending test jobs before upgrading the test printer to PDF'};
        $p=Get-CimInstance Win32_TCPIPPrinterPort | Where-Object Name -eq $a.Port | Select-Object -First 1;
        if($p -and ($p.HostAddress -ne '127.0.0.1' -or $p.Protocol -ne 2 -or $p.Queue -ne $a.Queue)){throw 'Test printer port belongs to another configuration'};
        if(!$p -and (Get-PrinterPort | Where-Object Name -eq $a.Port)){throw 'Test printer port is already in use'};
        """ + EnsureDriverScript + """
        if(!$p){Add-PrinterPort -Name $a.Port -LprHostAddress '127.0.0.1' -LprQueueName $a.Queue -LprByteCounting};
        if(!$q){Add-Printer -Name $a.Name -DriverName $a.Driver -PortName $a.Port -PrintProcessor 'winprint' -Datatype 'RAW' -Comment 'Remote Print PDF test printer'}
        elseif($q.DriverName -eq $a.Legacy){Set-Printer -Name $a.Name -DriverName $a.Driver -Comment 'Remote Print PDF test printer'; return 'UPGRADED'};
        'OK'
        """, new { Name = TestPrinter.Name, Port = TestPrinter.PortName, Driver = TestPrinter.DriverName, Legacy = TestPrinter.LegacyDriverName,
            Queue = TestPrinter.QueueId, ExistingOnly = existingOnly }, ct);
}
