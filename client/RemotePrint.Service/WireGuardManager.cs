using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;
using RemotePrint.Client;

namespace RemotePrint.Service;

// Runs as the RemotePrint LocalSystem service. WireGuard's tunnel has its own
// service; all files below are in the service's SYSTEM/Administrators-only folder.
public sealed class WireGuardManager
{
    private readonly string _directory, _name, _wireguard, _wg;
    private string? _publicKey;
    private string? _meshBaseHash;
    private IReadOnlyList<MeshBinding> _meshPeers = [];
    private string ConfigPath => Path.Combine(_directory, _name + ".conf.dpapi");
    private string KeyPath => Path.Combine(_directory, "wireguard-private.dpapi");
    private string HashPath => Path.Combine(_directory, _name + ".sha256");
    private string BaseHashPath => Path.Combine(_directory, _name + ".base.sha256");
    private string ServiceName => "WireGuardTunnel$" + _name;
    private static readonly byte[] KeyEntropy = "RemotePrint.WireGuard.v1"u8.ToArray();

    public WireGuardManager(string directory, string deviceId, string? wireGuardDirectory = null)
    {
        if (!Guid.TryParse(deviceId, out var id)) throw new ArgumentException("Mã thiết bị không hợp lệ.");
        _directory = directory;
        _name = "RemotePrint" + id.ToString("N")[..12];
        var binaryDir = wireGuardDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard");
        _wireguard = Path.Combine(binaryDir, "wireguard.exe");
        _wg = Path.Combine(binaryDir, "wg.exe");
    }

    public bool Available => File.Exists(_wireguard) && File.Exists(_wg);
    public string TunnelName => _name;

    public async Task InstallAsync(CancellationToken ct)
    {
        if (Available) return;
        var expectedHash = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "7BFED60AD61B785C914B38B61555A975488E1D3EC472DBFB2FCDF498FCA75242",
            Architecture.Arm64 => "8336335C738D4AFF040FA9D11B752DA65A80B6F41D3262D6BA5E82F97EBD1BD4",
            _ => throw new PlatformNotSupportedException("Kiến trúc máy không được hỗ trợ bởi WireGuard.")
        };
        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "WireGuardSetup.msi"));
        if (!File.Exists(package)) throw new FileNotFoundException("Thiếu bộ cài WireGuard trong gói Remote Print.");
        await using (var stream = File.OpenRead(package))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (actual != expectedHash) throw new InvalidDataException("Bộ cài WireGuard không đúng phiên bản được xác minh.");
        }
        await RunAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            ["/i", package, "/qn", "/norestart", "DO_NOT_LAUNCH=1"], null, ct, TimeSpan.FromMinutes(5));
        if (!Available) throw new InvalidOperationException("Cài WireGuard không tạo được các tệp cần thiết.");
    }

    public async Task<string> PublicKeyAsync(CancellationToken ct)
    {
        if (_publicKey is not null) return _publicKey;
        var key = await PrivateKeyAsync(ct);
        var publicKey = (await RunAsync(_wg, ["pubkey"], key + "\n", ct)).Trim();
        if (!WireGuardConfig.ValidKey(publicKey)) throw new InvalidDataException("WireGuard trả khóa công khai không hợp lệ.");
        return _publicKey = publicKey;
    }

    private async Task<string> PrivateKeyAsync(CancellationToken ct)
    {
        if (File.Exists(KeyPath))
        {
            var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(KeyPath, ct), KeyEntropy, DataProtectionScope.LocalMachine);
            try
            {
                var key = Encoding.ASCII.GetString(plain);
                if (!WireGuardConfig.ValidKey(key)) throw new InvalidDataException("Khóa WireGuard đã lưu không hợp lệ.");
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        var created = (await RunAsync(_wg, ["genkey"], null, ct)).Trim();
        if (!WireGuardConfig.ValidKey(created)) throw new InvalidDataException("WireGuard tạo khóa không hợp lệ.");
        var bytes = Encoding.ASCII.GetBytes(created);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, KeyEntropy, DataProtectionScope.LocalMachine);
            var temporary = KeyPath + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, encrypted, ct);
                File.Move(temporary, KeyPath, true);
            }
            finally { File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return created;
    }

    public async Task EnsureAsync(TunnelInfo info, IEnumerable<string> peerAddresses, CancellationToken ct)
    {
        var key = await PrivateKeyAsync(ct);
        var addresses = peerAddresses.ToArray();
        var config = WireGuardConfig.Build(key, info.Address, info.ServerPublicKey, info.Endpoint, addresses);
        // Endpoint, key or local-address changes still need a tunnel reinstall;
        // membership changes can be applied in place through wg.exe.
        var baseConfig = WireGuardConfig.Build(key, info.Address, info.ServerPublicKey, info.Endpoint);
        var baseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseConfig)));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
        var exists = ServiceExists();
        if (exists) EnsureOwnedService();
        if (exists && File.Exists(ConfigPath) && File.Exists(BaseHashPath) &&
            File.ReadAllText(BaseHashPath) == baseHash)
        {
            if (!File.Exists(HashPath) || File.ReadAllText(HashPath) != hash)
            {
                await WriteConfigAsync(config, ct);
                EnsureRunning();
                await RunAsync(_wg, ["set", _name, "peer", info.ServerPublicKey, "allowed-ips",
                    WireGuardConfig.AllowedIps(info.Address, addresses)], null, ct);
                await File.WriteAllTextAsync(HashPath, hash, ct);
            }
            else EnsureRunning();
            return;
        }
        if (exists) await RunAsync(_wireguard, ["/uninstalltunnelservice", _name], null, ct);
        await WriteConfigAsync(config, ct);
        await RunAsync(_wireguard, ["/installtunnelservice", ConfigPath], null, ct);
        EnsureRunning();
        await File.WriteAllTextAsync(BaseHashPath, baseHash, ct);
        await File.WriteAllTextAsync(HashPath, hash, ct);
    }

    public async Task EnsureMeshAsync(string address, IReadOnlyList<MeshBinding> peers, int listenPort, CancellationToken ct)
    {
        var key = await PrivateKeyAsync(ct);
        var config = WireGuardConfig.BuildMesh(key, address, peers, listenPort);
        var baseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WireGuardConfig.BuildMesh(key, address, [], listenPort))));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
        if (ServiceExists())
        {
            EnsureOwnedService();
            if (_meshBaseHash == baseHash)
            {
                EnsureRunning();
                if (File.Exists(HashPath) && File.ReadAllText(HashPath) == hash) return;
                var adapter = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name.Equals(_name, StringComparison.OrdinalIgnoreCase));
                var index = adapter.GetIPProperties().GetIPv4Properties().Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (var retired in _meshPeers.Where(p => !peers.Any(next => next.PublicKey == p.PublicKey && next.Address == p.Address)))
                    await RunAsync(_wg, ["set", _name, "peer", retired.PublicKey, "remove"], null, ct);
                foreach (var retired in _meshPeers.Where(p => !peers.Any(next => next.Address == p.Address)))
                    await RouteAsync("delete", retired.Address, index, ct);
                foreach (var added in peers.Where(p => !_meshPeers.Any(old => old.Address == p.Address)))
                    await RouteAsync("add", added.Address, index, ct);
                foreach (var peer in peers.Where(p => !_meshPeers.Any(old => old.PublicKey == p.PublicKey && old.Address == p.Address && old.Port == p.Port)))
                    await RunAsync(_wg, ["set", _name, "peer", peer.PublicKey, "allowed-ips", peer.Address + "/32",
                        "endpoint", "127.0.0.1:" + peer.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "persistent-keepalive", "15"], null, ct);
                await WriteConfigAsync(config, ct);
                await File.WriteAllTextAsync(HashPath, hash, ct);
                _meshPeers = peers.ToArray();
                return;
            }
            // A new runtime/private identity takes ownership by reinstalling
            // once. Later Room updates change keys/routes without tearing down
            // healthy connections to the other peers.
            await RunAsync(_wireguard, ["/uninstalltunnelservice", _name], null, ct);
        }
        await WriteConfigAsync(config, ct);
        await RunAsync(_wireguard, ["/installtunnelservice", ConfigPath], null, ct);
        EnsureRunning();
        await File.WriteAllTextAsync(HashPath, hash, ct);
        File.Delete(BaseHashPath);
        _meshBaseHash = baseHash;
        _meshPeers = peers.ToArray();
    }

    private static Task<string> RouteAsync(string action, string address, string index, CancellationToken ct) =>
        RunAsync(Path.Combine(Environment.SystemDirectory, "netsh.exe"), ["interface", "ipv4", action, "route",
            "prefix=" + address + "/32", "interface=" + index, "nexthop=0.0.0.0", "store=active"], null, ct);

    public async Task<HashSet<string>> RecentMeshHandshakesAsync(CancellationToken ct)
    {
        var output = await RunAsync(_wg, ["show", _name, "latest-handshakes"], null, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t')[0]).Where(key => HasRecentHandshake(output, key, now)).ToHashSet(StringComparer.Ordinal);
    }

    private void EnsureRunning()
    {
        using var service = new ServiceController(ServiceName);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    private async Task WriteConfigAsync(string config, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(config);
        var temporary = ConfigPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, ProtectForWireGuard(bytes, _name), ct);
            File.Move(temporary, ConfigPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            File.Delete(temporary);
        }
    }

    public async Task<bool> HasRecentHandshakeAsync(string serverKey, CancellationToken ct)
    {
        var output = await RunAsync(_wg, ["show", _name, "latest-handshakes"], null, ct);
        return HasRecentHandshake(output, serverKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public static bool HasRecentHandshake(string output, string serverKey, long now)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length == 2 && fields[0] == serverKey && long.TryParse(fields[1], out var time))
                return time > 0 && time <= now + 30 && now - time <= 180;
        }
        return false;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _meshBaseHash = null;
        _meshPeers = [];
        if (!ServiceExists()) return;
        if (!Available) throw new InvalidOperationException("Không tìm thấy WireGuard để dừng tunnel đã cài.");
        EnsureOwnedService();
        await RunAsync(_wireguard, ["/uninstalltunnelservice", _name], null, ct);
    }

    public async Task ResetIdentityAsync(CancellationToken ct)
    {
        await StopAsync(ct);
        File.Delete(KeyPath);
        File.Delete(ConfigPath);
        File.Delete(HashPath);
        File.Delete(BaseHashPath);
        _publicKey = null;
    }

    private bool ServiceExists()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        return key is not null;
    }

    private void EnsureOwnedService()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
        var binary = key?.GetValue("ImagePath") as string;
        if (binary is null || !binary.Contains(ConfigPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tên dịch vụ WireGuard đã được sử dụng bởi ứng dụng khác.");
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, string? input, CancellationToken ct, TimeSpan? limit = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Không khởi chạy được WireGuard.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(45));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"WireGuard lỗi ({process.ExitCode}): {error.Trim()}");
            return output;
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] ProtectForWireGuard(byte[] input, string name)
    {
        var blob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, blob.Data, input.Length);
            if (!CryptProtectData(ref blob, name, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 5, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, output.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(blob.Data, i, 0);
            Marshal.FreeHGlobal(blob.Data);
        }
    }
}
