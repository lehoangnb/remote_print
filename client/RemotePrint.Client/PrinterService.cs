using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePrint.Client;

public static class PrinterService
{
    private const uint PrinterEnumLocal = 0x00000002;
    private const uint PrinterEnumConnections = 0x00000004;
    private const uint PrinterAttributeShared = 0x00000008;

    // Upgrade-only cleanup. No new queue/driver/port can be installed by this
    // code; it removes only the previous product's known capture queue.
    public static void RemoveObsoleteVirtualQueue()
    {
        var defaults = new PrinterDefaults { DesiredAccess = 0x0001000C }; // DELETE | ADMINISTER | USE
        if (!OpenPrinter("Remote Print", out var printer, ref defaults))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 1801 or 2) return; // queue absent
            throw new Win32Exception(error, "Không kiểm tra được hàng đợi Remote Print cũ.");
        }
        var buffer = IntPtr.Zero;
        try
        {
            _ = GetPrinter(printer, 2, IntPtr.Zero, 0, out var bytes);
            if (bytes < Marshal.SizeOf<PrinterInfo2>()) throw new Win32Exception(Marshal.GetLastWin32Error());
            buffer = Marshal.AllocHGlobal(bytes);
            if (!GetPrinter(printer, 2, buffer, bytes, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
            var port = Marshal.PtrToStringUni(info.PortName);
            var spool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemotePrint", "Spool");
            if (!string.Equals(port, "RemotePrintCapture:", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(port, Path.Combine(spool, "capture.oxps"), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(port, Path.Combine(spool, "capture.pdf"), StringComparison.OrdinalIgnoreCase)) return;
            if (info.Jobs != 0) throw new InvalidOperationException("Hàng đợi Remote Print cũ còn lệnh in. Hoàn tất hoặc hủy các lệnh đó trước khi cập nhật.");
            if (!DeletePrinter(printer)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Không gỡ được hàng đợi Remote Print cũ.");
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); ClosePrinter(printer); }
    }

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeletePrinter(IntPtr printer);

    public static void SetWindowsPrinterShare(string printerName, string? expectedShare, string? share)
    {
        var defaults = new PrinterDefaults { DesiredAccess = 0x0000000C }; // ADMINISTER | USE
        if (!OpenPrinter(printerName, out var printer, ref defaults))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không mở được máy in để thay đổi chia sẻ.");
        IntPtr buffer = IntPtr.Zero, sharePointer = IntPtr.Zero;
        try
        {
            _ = GetPrinter(printer, 2, IntPtr.Zero, 0, out var needed);
            if (needed < Marshal.SizeOf<PrinterInfo2>())
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Không đọc được cấu hình máy in {printerName}.");
            buffer = Marshal.AllocHGlobal(needed);
            if (!GetPrinter(printer, 2, buffer, needed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Không đọc được cấu hình máy in {printerName}.");
            var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
            var currentShare = (info.Attributes & PrinterAttributeShared) == 0 ? null : Marshal.PtrToStringUni(info.ShareName);
            if (!string.Equals(currentShare, expectedShare, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Chia sẻ máy in đã thay đổi; hãy quét lại trước khi tiếp tục.");
            if (share is null)
            {
                info.Attributes &= ~PrinterAttributeShared;
                info.ShareName = IntPtr.Zero;
            }
            else
            {
                if (!IsUsableShareName(share)) throw new InvalidDataException("Tên share không hợp lệ.");
                sharePointer = Marshal.StringToHGlobalUni(share);
                info.ShareName = sharePointer;
                info.Attributes |= PrinterAttributeShared;
            }
            // Preserve the printer's existing ACL: SetPrinter with a null security
            // descriptor does not attempt to replace it.
            info.SecurityDescriptor = IntPtr.Zero;
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetPrinter(printer, 2, buffer, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows từ chối thay đổi chia sẻ máy in.");
        }
        finally
        {
            if (sharePointer != IntPtr.Zero) Marshal.FreeHGlobal(sharePointer);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            ClosePrinter(printer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterDefaults { public IntPtr DataType, DevMode; public uint DesiredAccess; }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out IntPtr printer, ref PrinterDefaults defaults);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPrinter(IntPtr printer, uint level, IntPtr buffer, int size, out int needed);
    [DllImport("winspool.drv", EntryPoint = "SetPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPrinter(IntPtr printer, uint level, IntPtr buffer, uint command);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr printer);

    // Only publish actual Windows shares, not merely queues selected in Remote Print.
    public static Dictionary<string, string> GetWindowsPrinterShares()
    {
        var ok = EnumPrinters(PrinterEnumLocal, null, 2, IntPtr.Zero, 0, out var bytes, out _);
        var error = Marshal.GetLastWin32Error();
        if (!ok && error != 122) throw new Win32Exception(error, "Windows không đọc được máy in chia sẻ.");
        if (bytes == 0)
        {
            if (!ok) throw new Win32Exception(error, "Windows không trả kích thước danh sách máy in.");
            return new(StringComparer.OrdinalIgnoreCase);
        }
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!EnumPrinters(PrinterEnumLocal, null, 2, buffer, bytes, out _, out var count))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PrinterInfo2>(IntPtr.Add(buffer, checked(i * Marshal.SizeOf<PrinterInfo2>())));
                var name = Marshal.PtrToStringUni(item.PrinterName);
                var share = Marshal.PtrToStringUni(item.ShareName);
                if ((item.Attributes & PrinterAttributeShared) != 0 && !string.IsNullOrWhiteSpace(name)
                    && IsUsableShareName(share)) result[name] = share!;
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsUsableShareName(string? share) =>
        !string.IsNullOrWhiteSpace(share) && share.Length <= 200 && share == share.Trim()
        && share is not ("." or "..") && !share.Any(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c));

    public static IReadOnlyList<string> GetServicePrinters(bool includeRemoteQueues = false)
    {
        const uint flags = PrinterEnumLocal;
        var ok = EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out var bytes, out _);
        var error = Marshal.GetLastWin32Error();
        if (!ok && error != 122) throw new Win32Exception(error, "Windows không đọc được danh sách máy in.");
        if (bytes == 0)
        {
            if (!ok) throw new Win32Exception(error, "Windows không trả kích thước danh sách máy in.");
            return [];
        }
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
                    || port.Contains("SHRFAX", StringComparison.OrdinalIgnoreCase) || (!includeRemoteQueues && port.StartsWith("RP_", StringComparison.OrdinalIgnoreCase))) continue;
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
        var ok = EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var requiredBytes, out _);
        var error = Marshal.GetLastWin32Error();
        if (!ok && error != 122)
            throw new Win32Exception(error, "Windows không đọc được danh sách máy in.");
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
