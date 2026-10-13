using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace RemotePrint.Client;

public sealed class ConfigService
{
    private static readonly byte[] Entropy = "RemotePrint.Client.v1"u8.ToArray();
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemotePrint", "Client");
    private string ConfigPath => Path.Combine(_directory, "config.json");

    public ClientConfig LoadOrCreate()
    {
        Directory.CreateDirectory(_directory);
        ClientConfig config;
        try { config = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(ConfigPath)) ?? new() : new(); }
        catch { config = new(); }
        if (string.IsNullOrWhiteSpace(config.PublicKey) || string.IsNullOrWhiteSpace(config.ProtectedPrivateKey))
        {
            using var rsa = RSA.Create(3072);
            config.PublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
            var privateKey = rsa.ExportPkcs8PrivateKey();
            config.ProtectedPrivateKey = Convert.ToBase64String(ProtectedData.Protect(privateKey, Entropy, DataProtectionScope.CurrentUser));
            CryptographicOperations.ZeroMemory(privateKey);
        }
        Save(config);
        return config;
    }

    public void Save(ClientConfig config)
    {
        Directory.CreateDirectory(_directory);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ConfigPath, true);
    }

    public string PrepareServiceMigration()
    {
        var config = LoadOrCreate();
        using var rsa = GetPrivateKey(config);
        var bytes = rsa.ExportPkcs8PrivateKey();
        try
        {
            config.ProtectedPrivateKey = Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine));
            var path = Path.Combine(_directory, $"service-migration-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(config));
            return path;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public RSA GetPrivateKey(ClientConfig config)
    {
        var protectedBytes = Convert.FromBase64String(config.ProtectedPrivateKey);
        var privateBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateBytes, out _);
        CryptographicOperations.ZeroMemory(privateBytes);
        return rsa;
    }
}

