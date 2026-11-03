using System.Text.Json.Serialization;

namespace RemotePrint.Client;

public sealed class ClientConfig
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public string DeviceSecret { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    public string ComputerName { get; set; } = $"PRINT-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))}";
    public string ServerUrl { get; set; } = "";
    public string EnrollmentEndpoint { get; set; } = "";
    public string EnrollmentKey { get; set; } = "";
    public List<string> SharedPrinters { get; set; } = [];
    public Dictionary<string, string> ManagedWindowsShares { get; set; } = new();
}

public sealed class ClientState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? Room { get; set; }
    public List<string> Rooms { get; set; } = [];
}

public sealed class PeerClient
{
    public bool NativePrinting { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Printers { get; set; } = [];
    public Dictionary<string, string> WindowsPrinterShares { get; set; } = new();
    public DateTime LastSeenAt { get; set; }
    public string? TunnelAddress { get; set; }
    public string TunnelPath { get; set; } = "";
    [JsonIgnore] public string PrinterSummary => string.Join(", ", Printers);
    [JsonIgnore] public string LastSeenDisplay => LastSeenAt.ToLocalTime().ToString("dd/MM HH:mm:ss");
}

public sealed class TunnelPeer
{
    public string Id { get; set; } = "";
    public string Address { get; set; } = "";
}

public sealed class TunnelInfo
{
    public bool EmbeddedStun { get; set; }
    public string Mode { get; set; } = "legacy";
    public string UdpHost { get; set; } = "";
    public int UdpPort { get; set; } = 51821;
    public string Address { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ServerPublicKey { get; set; } = "";
    public string EnrolledPublicKey { get; set; } = "";
    public List<TunnelPeer> Peers { get; set; } = [];
}

