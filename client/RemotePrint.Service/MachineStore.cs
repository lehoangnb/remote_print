using System.Security.Cryptography;
using System.Text.Json;
using RemotePrint.Client;

namespace RemotePrint.Service;

public sealed class MachineStore
{
    public string DirectoryPath { get; }
    public MachineStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemotePrint", "Service");
    private static readonly byte[] Entropy = "RemotePrint.Client.v1"u8.ToArray();
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");

    public ClientConfig Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(ConfigPath))
            return JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(ConfigPath))
                ?? throw new InvalidDataException("Cấu hình service không hợp lệ.");
        using var rsa = RSA.Create(3072);
        var privateBytes = rsa.ExportPkcs8PrivateKey();
        var config = new ClientConfig
        {
            PublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
            ProtectedPrivateKey = Convert.ToBase64String(ProtectedData.Protect(privateBytes, Entropy, DataProtectionScope.LocalMachine))
        };
        CryptographicOperations.ZeroMemory(privateBytes);
        Save(config);
        return config;
    }

    public void Save(ClientConfig config)
    {
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config));
        File.Move(tmp, ConfigPath, true);
    }

    public RSA GetPrivateKey(ClientConfig config)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(config.ProtectedPrivateKey), Entropy, DataProtectionScope.LocalMachine);
        try { var rsa = RSA.Create(); rsa.ImportPkcs8PrivateKey(bytes, out _); return rsa; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
