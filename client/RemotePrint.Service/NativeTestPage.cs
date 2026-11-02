using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePrint.Service;

// Exercise GDI -> Windows printer driver, just as Word does. Sending a literal
// RAW string would bypass the renderer and hide the old text-only driver defect.
public static class NativeTestPage
{
    public static int Print(string printer, string destination, Func<bool> authorized)
    {
        var dc = CreateDC("WINSPOOL", printer, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var complete = false;
        var started = false;
        IntPtr font = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (!authorized()) throw new UnauthorizedAccessException("Print permission revoked");
            var doc = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = "Remote Print PDF test" };
            var job = StartDoc(dc, ref doc);
            if (job <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            started = true;
            if (StartPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var dpiX = GetDeviceCaps(dc, 88); var dpiY = GetDeviceCaps(dc, 90);
            font = CreateFont(-16 * dpiY / 72, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Arial");
            if (font == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(dc, font);
            var lines = new[] { "REMOTE PRINT PDF TEST", "Kiểm tra chữ tiếng Việt: Xin chào!", $"From: {Environment.MachineName}",
                $"To: {destination}", $"UTC: {DateTime.UtcNow:O}", $"Test: {Guid.NewGuid():N}", "Text, fonts and this rectangle should be visible." };
            for (var index = 0; index < lines.Length; index++)
                if (!TextOut(dc, dpiX / 2, dpiY / 2 + index * dpiY / 3, lines[index], lines[index].Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Rectangle(dc, dpiX / 2, dpiY * 3, dpiX * 5, dpiY * 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!authorized()) throw new UnauthorizedAccessException("Print permission revoked");
            if (EndPage(dc) <= 0 || EndDoc(dc) <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            complete = true;
            return job;
        }
        finally
        {
            if (started && !complete) AbortDoc(dc);
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            if (font != IntPtr.Zero) DeleteObject(font);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Output;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
        public int Type;
    }
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr devmode);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDoc(IntPtr dc, ref DocInfo info);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFont(int height, int width, int angle, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint outputPrecision, uint clipPrecision, uint quality, uint pitch, string name);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "TextOutW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool Rectangle(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
