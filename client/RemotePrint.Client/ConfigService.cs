using System.IO;
using System.Text.Json;

namespace RemotePrint.Client;

public sealed class ConfigService
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemotePrint", "Client");
    private string ConfigPath => Path.Combine(_directory, "config.json");

    public ClientConfig LoadOrCreate()
    {
        Directory.CreateDirectory(_directory);
        ClientConfig config;
        try { config = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(ConfigPath)) ?? new() : new(); }
        catch { config = new(); }
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
        var path = Path.Combine(_directory, $"service-migration-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        return path;
    }
}
