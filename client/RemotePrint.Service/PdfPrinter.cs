using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePrint.Service;

// PDFium rasterizes pages; Windows GDI submits the rendered pages without any UI.
public static class PdfPrinter
{
    private static readonly object Sync = new();

    public static void Print(string path, string printerName, string? outputPath = null)
    {
        lock (Sync)
        {
            FPDF_InitLibrary();
            var bytes = File.ReadAllBytes(path);
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr document = IntPtr.Zero, dc = IntPtr.Zero;
            var started = false;
            try
            {
                document = FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), bytes.Length, null);
                if (document == IntPtr.Zero) throw new InvalidDataException($"PDF không hợp lệ hoặc cần mật khẩu (mã {FPDF_GetLastError()}).");
                var count = FPDF_GetPageCount(document);
                if (count < 1 || count > 2000) throw new InvalidDataException("Số trang PDF không hợp lệ.");
                dc = CreateDC("WINSPOOL", printerName, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Không mở được máy in {printerName}.");
                var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = "Remote Print", Output = outputPath };
                Check(StartDoc(dc, ref info), "Không tạo được lệnh in.");
                started = true;
                for (var index = 0; index < count; index++) RenderPage(document, index, dc);
                Check(EndDoc(dc), "Không hoàn tất được lệnh in.");
                started = false;
            }
            finally
            {
                if (started) AbortDoc(dc);
                if (dc != IntPtr.Zero) DeleteDC(dc);
                if (document != IntPtr.Zero) FPDF_CloseDocument(document);
                pinned.Free();
                FPDF_DestroyLibrary();
            }
        }
    }

    private static void RenderPage(IntPtr document, int index, IntPtr dc)
    {
        var page = FPDF_LoadPage(document, index);
        if (page == IntPtr.Zero) throw new InvalidDataException($"Không mở được trang {index + 1}.");
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var pageWidth = FPDF_GetPageWidth(page);
            var pageHeight = FPDF_GetPageHeight(page);
            if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
                throw new InvalidDataException("Kích thước trang PDF không hợp lệ.");
            var width = GetDeviceCaps(dc, 8); // HORZRES: printable area in printer pixels
            var height = GetDeviceCaps(dc, 10);
            var dpiX = GetDeviceCaps(dc, 88);
            var dpiY = GetDeviceCaps(dc, 90);
            if (width <= 0 || height <= 0 || dpiX <= 0 || dpiY <= 0) throw new InvalidOperationException("Máy in trả về khổ giấy không hợp lệ.");
            var fit = Math.Min(width / (pageWidth * dpiX / 72), height / (pageHeight * dpiY / 72));
            var targetWidth = Math.Max(1, (int)(pageWidth * dpiX / 72 * fit));
            var targetHeight = Math.Max(1, (int)(pageHeight * dpiY / 72 * fit));
            // Bound memory use and render at up to 300 dpi.
            var rasterScale = Math.Min(1, Math.Min(300.0 / Math.Max(dpiX, dpiY), 4000.0 / Math.Max(targetWidth, targetHeight)));
            var rasterWidth = Math.Max(1, (int)(targetWidth * rasterScale));
            var rasterHeight = Math.Max(1, (int)(targetHeight * rasterScale));
            bitmap = FPDFBitmap_Create(rasterWidth, rasterHeight, 0);
            if (bitmap == IntPtr.Zero) throw new OutOfMemoryException("Không tạo được ảnh trang PDF.");
            FPDFBitmap_FillRect(bitmap, 0, 0, rasterWidth, rasterHeight, 0xFFFFFFFF);
            FPDF_RenderPageBitmap(bitmap, page, 0, 0, rasterWidth, rasterHeight, 0, 0x801); // printing + annotations
            var bits = new BitmapInfo
            {
                Size = 40, Width = rasterWidth, Height = -rasterHeight, Planes = 1, BitCount = 32,
                SizeImage = checked((uint)(rasterWidth * rasterHeight * 4))
            };
            Check(StartPage(dc), "Không bắt đầu được trang in.");
            SetStretchBltMode(dc, 4); // HALFTONE
            var result = StretchDIBits(dc, (width - targetWidth) / 2, (height - targetHeight) / 2,
                targetWidth, targetHeight, 0, 0, rasterWidth, rasterHeight,
                FPDFBitmap_GetBuffer(bitmap), ref bits, 0, 0x00CC0020);
            if (result is 0 or -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Không gửi được ảnh trang tới máy in.");
            Check(EndPage(dc), "Không hoàn tất được trang in.");
        }
        finally { if (bitmap != IntPtr.Zero) FPDFBitmap_Destroy(bitmap); FPDF_ClosePage(page); }
    }

    private static void Check(int result, string message)
    { if (result <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), message); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo { public int Size; public string Name; public string? Output; public string? DataType; public uint Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("pdfium")] private static extern void FPDF_InitLibrary();
    [DllImport("pdfium")] private static extern void FPDF_DestroyLibrary();
    [DllImport("pdfium")] private static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport("pdfium")] private static extern uint FPDF_GetLastError();
    [DllImport("pdfium")] private static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport("pdfium")] private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport("pdfium")] private static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport("pdfium")] private static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport("pdfium")] private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport("pdfium")] private static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport("pdfium")] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
    [DllImport("pdfium")] private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport("pdfium")] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport("pdfium")] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium")] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport("gdi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr mode);
    [DllImport("gdi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDoc(IntPtr dc, ref DocInfo info);
    [DllImport("gdi32", SetLastError = true)] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32")] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32", SetLastError = true)] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32", SetLastError = true)] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32", SetLastError = true)] private static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, IntPtr bits, ref BitmapInfo info, uint usage, uint rop);
}
