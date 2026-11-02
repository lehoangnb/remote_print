using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RemotePrint.Setup;

// This executable deliberately targets the Windows .NET Framework. It can download
// and install the client's .NET runtime before attempting to launch any net8 binary.
internal sealed class Release
{
    public string version { get; set; } = "";
    public string runtime { get; set; } = "";
    public long size { get; set; }
    public string sha256 { get; set; } = "";
    public string downloadPath { get; set; } = "";
}

internal sealed class Bootstrapper : IDisposable
{
    private const long MaxSize = 256L * 1024 * 1024;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = 16 * 1024 * 1024 };
    internal static string Runtime
    {
        get
        {
            var arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            if (string.Equals(arch, "ARM64", StringComparison.OrdinalIgnoreCase)) return "win-arm64";
            if (string.Equals(arch, "AMD64", StringComparison.OrdinalIgnoreCase)) return "win-x64";
            throw new InvalidOperationException(Texts.T("Bộ cài chỉ hỗ trợ Windows x64 và ARM64."));
        }
    }

    internal static Uri ServerUri(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException(Texts.T("Hãy nhập địa chỉ server HTTPS."));
        return uri;
    }

    internal async Task<Release> DownloadAsync(string server, string directory, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var origin = ServerUri(server);
        progress.Report(Texts.T("Đang kiểm tra bản mới nhất…"));
        using var response = await http.GetAsync(new Uri(origin, "api/client/updates/" + Runtime), HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(Texts.T("Server chưa phát hành gói client cho kiến trúc máy này."));
        response.EnsureSuccessStatusCode();
        var metadata = await ReadLimitedAsync(response, 16384, ct);
        var release = Json.Deserialize<Release>(metadata);
        if (release == null || !Regex.IsMatch(release.version ?? "", @"^(0|[1-9]\d{0,4})(\.(0|[1-9]\d{0,4})){2,3}$") ||
            !Version.TryParse(release.version, out var version) || version.Major > 65534 || version.Minor > 65534 || version.Build > 65534 || version.Revision > 65534 ||
            release.runtime != Runtime || release.size <= 0 || release.size > MaxSize ||
            !Regex.IsMatch(release.sha256 ?? "", "^[a-fA-F0-9]{64}$") ||
            release.downloadPath != "/api/client/updates/packages/" + release.sha256!.ToLowerInvariant() + ".zip")
            throw InvalidPackage();
        progress.Report(Texts.T("Đang tải client…") + " " + release.version + " (" + release.runtime + ")");
        var archive = Path.Combine(directory, "client.zip");
        await DownloadFileAsync(new Uri(origin, release.downloadPath.TrimStart('/')), archive, release.size, release.sha256!, false, progress, ct);
        progress.Report(Texts.T("Đang kiểm tra gói cài đặt…"));
        ExtractAndVerify(archive, Path.Combine(directory, "package"), release, ct);
        File.WriteAllText(Path.Combine(directory, "release.json"), Json.Serialize(release));
        return release;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        using var input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            if (output.Length + count > limit) throw InvalidPackage();
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
    }

    private async Task DownloadFileAsync(Uri uri, string path, long size, string expected, bool sha512, IProgress<string> progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (size > 0 && response.Content.Headers.ContentLength is long length && length != size) throw InvalidPackage();
        using var input = await response.Content.ReadAsStreamAsync();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using HashAlgorithm hash = sha512 ? SHA512.Create() : SHA256.Create();
        var buffer = new byte[81920];
        long total = 0; int count, lastPercent = -1;
        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            total += count;
            if (total > MaxSize || (size > 0 && total > size)) throw InvalidPackage();
            hash.TransformBlock(buffer, 0, count, buffer, 0);
            await output.WriteAsync(buffer, 0, count, ct);
            if (size > 0 && (int)(total * 100 / size) != lastPercent)
            {
                lastPercent = (int)(total * 100 / size);
                progress.Report(Texts.T("Đang tải…") + " " + lastPercent + "%");
            }
        }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if ((size > 0 && total != size) || !Hex(hash.Hash!).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw InvalidPackage();
    }

    internal static void ExtractAndVerify(string archive, string target, Release release, CancellationToken ct)
    {
        if (Directory.Exists(target)) throw new IOException("Package directory already exists.");
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.OpenRead(archive))
        {
            if (zip.Entries.Count > 10000) throw InvalidPackage();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith("/") || name.Contains(":") || name.Split('/').Any(p => p == ".." || p == "." || p.EndsWith(" ") || p.EndsWith(".")) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                    ((FileAttributes)entry.ExternalAttributes & FileAttributes.ReparsePoint) != 0 || !names.Add(name)) throw InvalidPackage();
                var path = Path.GetFullPath(Path.Combine(target, name));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw InvalidPackage();
                if (name.EndsWith("/")) { Directory.CreateDirectory(path); continue; }
                total = checked(total + entry.Length);
                if (total > 1024L * 1024 * 1024) throw InvalidPackage();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path);
            }
        }
        var manifestPath = Path.Combine(target, "build-info.json");
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw InvalidPackage();
        var data = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath));
        if ((string)data["version"] != release.version || (string)data["runtime"] != release.runtime || (string)data["protocol"] != "native-raw-v1") throw InvalidPackage();
        var files = ((Dictionary<string, object>)data["files"]).ToDictionary(p => p.Key.Replace('\\', '/'), p => (string)p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { "RemotePrint.Client.exe", "RemotePrint.Client.dll", "Service/RemotePrint.Service.exe", "Service/RemotePrint.Service.dll", "translations.json" })
            if (!files.ContainsKey(required)) throw InvalidPackage();
        int verified = 0;
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = file.Substring(root.Length).Replace('\\', '/');
            if (relative.Equals("build-info.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!files.TryGetValue(relative, out var expected) || !FileHash(file, false).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw InvalidPackage();
            verified++;
        }
        if (verified != files.Count) throw InvalidPackage();
        var normalized = Version.Parse(release.version + (release.version.Count(c => c == '.') == 2 ? ".0" : ""));
        foreach (var dll in new[] { "RemotePrint.Client.dll", "Service/RemotePrint.Service.dll" })
            if (AssemblyName.GetAssemblyName(Path.Combine(target, dll)).Version != normalized) throw InvalidPackage();
    }

    internal static bool HasDesktopRuntime(string runtime)
    {
        // SDK/manual installations need not register sharedfx in the registry. Ask
        // the native machine host, which is also used by the installed apphost.
        if (runtime != Runtime) return false;
        var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(host)) return false;
        using var process = Process.Start(new ProcessStartInfo(host, "--list-runtimes")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        if (process == null) return false;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); return false; }
        if (process.ExitCode != 0) return false;
        var lines = output.GetAwaiter().GetResult().Split('\n');
        return new[] { "Microsoft.WindowsDesktop.App", "Microsoft.NETCore.App" }.All(framework =>
            lines.Any(line => Regex.IsMatch(line, "^" + Regex.Escape(framework) + @" 8\.0\.\d+ \[")));
    }

    internal async Task<Dictionary<string, object>> RuntimeInstallerAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var data = Json.Deserialize<Dictionary<string, object>>(await ReadLimitedAsync(response, 16 * 1024 * 1024, ct));
        var latest = (string)data["latest-release"];
        var release = ((System.Collections.IEnumerable)data["releases"]).Cast<Dictionary<string, object>>().First(r => (string)r["release-version"] == latest);
        var desktop = (Dictionary<string, object>)release["windowsdesktop"];
        var file = ((System.Collections.IEnumerable)desktop["files"]).Cast<Dictionary<string, object>>().First(f => (string)f["rid"] == Runtime && ((string)f["name"]).EndsWith(".exe"));
        var uri = new Uri((string)file["url"]);
        if (uri.Scheme != "https" || !(uri.Host == "builds.dotnet.microsoft.com" || uri.Host == "download.visualstudio.microsoft.com" || uri.Host == "download.microsoft.com") ||
            !Regex.IsMatch((string)file["hash"], "^[a-fA-F0-9]{128}$")) throw InvalidPackage();
        return file;
    }

    internal async Task EnsureRuntimeAsync(string directory, IProgress<string> progress, CancellationToken ct)
    {
        if (HasDesktopRuntime(Runtime)) return;
        progress.Report(Texts.T("Đang tải .NET 8 Desktop Runtime từ Microsoft…"));
        var file = await RuntimeInstallerAsync(ct);
        var uri = new Uri((string)file["url"]);
        var installer = Path.Combine(directory, "windowsdesktop-runtime.exe");
        await DownloadFileAsync(uri, installer, 0, (string)file["hash"], true, progress, ct);
        ct.ThrowIfCancellationRequested();
        progress.Report(Texts.T("Đang cài .NET 8 Desktop Runtime…"));
        var code = await RunElevatedAsync(installer, "/install /quiet /norestart");
        if ((code != 0 && code != 3010) || !HasDesktopRuntime(Runtime))
            throw new InvalidOperationException(Texts.T("Không cài được .NET Desktop Runtime. Mã lỗi: ") + code);
    }

    internal static async Task<string> InstallAsync(string directory, IProgress<string> progress)
    {
        progress.Report(Texts.T("Đang cài dịch vụ Remote Print…"));
        var result = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemotePrint", "SetupResults", "remoteprint-setup-" + Guid.NewGuid().ToString("N") + ".json");
        var executable = Path.Combine(directory, "package", "RemotePrint.Client.exe");
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        try
        {
            var code = await RunElevatedAsync(executable, "--setup InstallService --result " + Quote(result) + " --user " + Quote(sid));
            if (!File.Exists(result)) throw new InvalidOperationException(Texts.T("Trình cài đặt không trả kết quả. Mã lỗi: ") + code);
            var data = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(result));
            if (code != 0 || !data.TryGetValue("ok", out var ok) || !(ok is bool success) || !success)
                throw new InvalidOperationException(data.TryGetValue("message", out var message) ? Convert.ToString(message) : Texts.T("Cài đặt thất bại."));
            var installed = (string)data["installedPath"];
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemotePrint", "versions") + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(installed).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(installed)) throw InvalidPackage();
            return installed;
        }
        finally { try { File.Delete(result); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal static void LaunchClient(string installed)
    {
        // The client uses a per-session singleton. An old installed UI must exit
        // before launching the new copy, or it would simply show the old window.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemotePrint", "versions") + Path.DirectorySeparatorChar;
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("RemotePrint.Client"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != self.SessionId) continue;
                    var path = process.MainModule?.FileName;
                    if (path == null || !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    if (!process.WaitForExit(10000)) throw new IOException(Texts.T("Hãy đóng client cũ rồi mở lại Remote Print."));
                }
                catch (InvalidOperationException) { } // It already exited.
                catch (System.ComponentModel.Win32Exception)
                { throw new IOException(Texts.T("Hãy đóng client cũ rồi mở lại Remote Print.")); }
            }
        }
        Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true });
    }

    private static async Task<int> RunElevatedAsync(string executable, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (process == null) throw new IOException("Cannot start installer.");
            await Task.Run(() => process.WaitForExit());
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        { throw new InvalidOperationException(Texts.T("Bạn đã hủy yêu cầu quyền quản trị.")); }
    }

    internal static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
    private static string FileHash(string path, bool sha512)
    {
        using var input = File.OpenRead(path);
        using HashAlgorithm hash = sha512 ? SHA512.Create() : SHA256.Create();
        return Hex(hash.ComputeHash(input));
    }
    private static InvalidDataException InvalidPackage() => new(Texts.T("Gói cài đặt không hợp lệ hoặc không toàn vẹn."));
    public void Dispose() => http.Dispose();
}
