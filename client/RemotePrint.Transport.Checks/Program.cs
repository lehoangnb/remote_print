using System.Text.Json;
using RemotePrint.Service;

// Test host for the exact production C# packet bridge on Linux. Secrets stay in
// a private configuration file; stdout/state files contain public bindings only.
if (args.Length != 2) throw new ArgumentException("Expected private-config.json public-state.json");
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var config = JsonSerializer.Deserialize<BridgeConfig>(await File.ReadAllTextAsync(args[0]), options) ?? throw new Exception("Invalid config");
await using var transport = new MeshTransport(new(config.ServerUrl, config.DeviceId, config.Secret),
    new(config.Address, config.UdpHost, config.UdpPort));
transport.SetWireGuardPort(config.WireGuardPort);
transport.Start();
while (true)
{
    // Changing a small marker switches paths without restarting either endpoint.
    transport.ForceRelay = File.Exists(args[0] + ".force-relay");
    var state = JsonSerializer.Serialize(new { authorized = transport.Authorized, bindings = transport.Bindings,
        localUdpPort = transport.LocalUdpPort, error = transport.Error }, options);
    await File.WriteAllTextAsync(args[1] + ".tmp", state);
    File.Move(args[1] + ".tmp", args[1], true);
    await Task.Delay(200);
}
record BridgeConfig(string ServerUrl, string DeviceId, string Secret, string Address, string UdpHost, int UdpPort, int WireGuardPort);
