using System.Text.Json.Serialization;

namespace RemotePrint.Client;

public sealed class ClientConfig
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public string DeviceSecret { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    public string ComputerName { get; set; } = $"PRINT-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))}";
    public string ServerUrl { get; set; } = "";
    public List<string> SharedPrinters { get; set; } = [];
    public string PublicKey { get; set; } = "";
    public string ProtectedPrivateKey { get; set; } = "";
}

public sealed class ClientState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? Room { get; set; }
}

public sealed class PeerClient
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public List<string> Printers { get; set; } = [];
    public DateTime LastSeenAt { get; set; }
    [JsonIgnore] public string PrinterSummary => string.Join(", ", Printers);
    [JsonIgnore] public string LastSeenDisplay => LastSeenAt.ToLocalTime().ToString("dd/MM HH:mm:ss");
}

public sealed class PrintJob
{
    public string Id { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class EncryptedPayload
{
    public string WrappedKey { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Ciphertext { get; set; } = "";
}

public sealed class JobPackage
{
    public PrintJob Job { get; set; } = new();
    public EncryptedPayload Payload { get; set; } = new();
}

public sealed class PrinterChoice
{
    public required string Name { get; set; }
    public bool IsSelected { get; set; }
}

