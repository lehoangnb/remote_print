using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePrint.Client;

public static class PrinterService
{
    private const uint PrinterEnumLocal = 0x00000002;
    private const uint PrinterEnumConnections = 0x00000004;

    public static IReadOnlyList<string> GetServicePrinters()
    {
        const uint flags = PrinterEnumLocal;
        _ = EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out var bytes, out _);
        if (bytes == 0) return [];
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!EnumPrinters(flags, null, 2, buffer, bytes, out _, out var count))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var names = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PrinterInfo2>(IntPtr.Add(buffer, checked(i * Marshal.SizeOf<PrinterInfo2>())));
                var name = Marshal.PtrToStringUni(item.PrinterName);
                var port = Marshal.PtrToStringUni(item.PortName);
                if (string.IsNullOrWhiteSpace(name) || name.Equals("Remote Print", StringComparison.OrdinalIgnoreCase)) continue;
                // Drivers requesting Save As / fax UI cannot be used in Session 0.
                if (port is null || port.Contains("PORTPROMPT", StringComparison.OrdinalIgnoreCase)
                    || port.Contains("SHRFAX", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(name);
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location, DevMode,
            SepFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
    }

    public static IReadOnlyList<string> GetInstalledPrinters()
    {
        const uint flags = PrinterEnumLocal | PrinterEnumConnections;
        _ = EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var requiredBytes, out _);
        if (requiredBytes == 0)
            return [];

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!EnumPrinters(flags, null, 4, buffer, requiredBytes, out _, out var count))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows không đọc được danh sách máy in.");

            var itemSize = Marshal.SizeOf<PrinterInfo4>();
            var printers = new List<string>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer, checked((int)index * itemSize)));
                var name = Marshal.PtrToStringUni(item.PrinterName);
                if (!string.IsNullOrWhiteSpace(name)) printers.Add(name);
            }
            return printers.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(
        uint flags,
        string? name,
        uint level,
        IntPtr printerEnum,
        uint bufferSize,
        out uint bytesNeeded,
        out uint printersReturned);
}
