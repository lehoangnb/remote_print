using System.Text;

namespace RemotePrint.Service;

public static class TestPrintFormat
{
    public static (string Format, bool HasContent) Inspect(string path)
    {
        using var file = File.OpenRead(path);
        var head = new byte[(int)Math.Min(4096, file.Length)];
        file.ReadExactly(head);
        if (head.AsSpan().StartsWith("%PDF-"u8))
        {
            var tail = new byte[(int)Math.Min(2048, file.Length)];
            file.Position = file.Length - tail.Length;
            file.ReadExactly(tail);
            if (!Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal))
                throw new InvalidDataException("File PDF in thử chưa hoàn chỉnh.");
            return ("pdf", true);
        }
        // Preserve old text output, including the one-byte form-feed, as evidence.
        // Do not report a control-only payload as a correctly rendered page.
        return ("txt", file.Length > head.Length || head.Any(value => value > 32 && value != 127));
    }
}
