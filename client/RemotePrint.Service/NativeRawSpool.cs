using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePrint.Service;

public static class NativeRawSpool
{
    public static int Print(string printerName, string file, Action<int> started, Func<bool> authorized)
    {
        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var complete = false;
        try
        {
            var doc = new DocInfo { Name = "Remote Print RAW", DataType = "RAW" };
            var id = StartDocPrinter(printer, 1, ref doc);
            if (id == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            started(id);
            using var stream = File.OpenRead(file); var buffer = new byte[65536]; int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (!authorized()) throw new UnauthorizedAccessException("Print permission revoked");
                if (!WritePrinter(printer, buffer, count, out var written) || written != count) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!authorized()) throw new UnauthorizedAccessException("Print permission revoked");
            if (!EndDocPrinter(printer)) throw new Win32Exception(Marshal.GetLastWin32Error());
            complete = true; return id;
        }
        finally { if (!complete) AbortPrinter(printer); ClosePrinter(printer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo { [MarshalAs(UnmanagedType.LPWStr)] public string Name; [MarshalAs(UnmanagedType.LPWStr)] public string? Output; [MarshalAs(UnmanagedType.LPWStr)] public string DataType; }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDocPrinter(IntPtr printer, int level, ref DocInfo doc);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr printer, byte[] data, int count, out int written);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(IntPtr printer);
    [DllImport("winspool.drv")] private static extern bool AbortPrinter(IntPtr printer);
    [DllImport("winspool.drv")] private static extern bool ClosePrinter(IntPtr printer);
}
