using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemotePrint.Client;

public sealed record EnrollmentCode(int Version, string ServerUrl, string Endpoint, string Key, DateTime ExpiresAt)
{
    public static EnrollmentCode? Parse(string text)
    {
        if (!text.StartsWith("RPPAIR1.", StringComparison.Ordinal)) return null;
        try
        {
            if (text.Length > 4096) throw new FormatException();
            var encoded = text[8..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var code = JsonSerializer.Deserialize<EnrollmentCode>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (code.Version != 1 || !Uri.TryCreate(code.ServerUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                !Regex.IsMatch(code.Endpoint, "^api/client/enroll/[a-f0-9]{32}$") || !Regex.IsMatch(code.Key, "^[A-Za-z0-9_-]{43}$")) throw new FormatException();
            if (code.ExpiresAt.ToUniversalTime() <= DateTime.UtcNow) throw new InvalidOperationException("Mã ghép nối đã hết hạn. Tạo mã mới trên server.");
            return code;
        }
        catch (InvalidOperationException) { throw; }
        catch { throw new InvalidOperationException("Mã ghép nối không hợp lệ."); }
    }
}
