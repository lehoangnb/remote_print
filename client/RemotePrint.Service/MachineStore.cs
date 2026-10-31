using System.Text.Json;
using RemotePrint.Client;

namespace RemotePrint.Service;

public sealed class MachineStore
{
    public string DirectoryPath { get; }
    public MachineStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemotePrint", "Service");
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");

    public ClientConfig Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(ConfigPath))
        {
            var existing = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(ConfigPath))
                ?? throw new InvalidDataException("Cấu hình service không hợp lệ.");
            Save(existing); // Normalize the configuration without changing device identity.
            return existing;
        }
        var config = new ClientConfig();
        Save(config);
        return config;
    }

    public void Save(ClientConfig config)
    {
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config));
        File.Move(tmp, ConfigPath, true);
    }

}
