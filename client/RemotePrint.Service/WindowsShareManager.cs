using System.Security.Cryptography;
using System.Text;
using RemotePrint.Client;

namespace RemotePrint.Service;

public interface IWindowsShareAccess
{
    Dictionary<string, string> ReadShares();
    void SetShare(string printer, string? expectedShare, string? newShare);
}

public sealed class WindowsShareAccess : IWindowsShareAccess
{
    public Dictionary<string, string> ReadShares() => PrinterService.GetWindowsPrinterShares();
    public void SetShare(string printer, string? expectedShare, string? newShare) =>
        PrinterService.SetWindowsPrinterShare(printer, expectedShare, newShare);
}

public sealed class WindowsShareManager(ClientConfig config, Action save, IWindowsShareAccess access)
{
    public bool Synchronize(bool enabled, IEnumerable<string>? availablePrinters = null)
    {
        var current = access.ReadShares();
        var selected = enabled ? config.SharedPrinters.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var available = availablePrinters?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? selected;
        var changed = false;
        foreach (var (printer, ownedShare) in config.ManagedWindowsShares.ToArray())
        {
            current.TryGetValue(printer, out var existing);
            if (selected.Contains(printer))
            {
                if (existing is null && available.Contains(printer))
                {
                    access.SetShare(printer, null, ownedShare);
                    current[printer] = ownedShare;
                    changed = true;
                }
                else if (existing is not null && !string.Equals(existing, ownedShare, StringComparison.OrdinalIgnoreCase))
                {
                    config.ManagedWindowsShares.Remove(printer);
                    save();
                }
            }
            else
            {
                if (string.Equals(existing, ownedShare, StringComparison.OrdinalIgnoreCase))
                {
                    access.SetShare(printer, ownedShare, null);
                    current.Remove(printer);
                    changed = true;
                }
                config.ManagedWindowsShares.Remove(printer);
                save();
            }
        }
        foreach (var printer in selected)
        {
            if (!available.Contains(printer) || current.ContainsKey(printer)) continue;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(printer.ToUpperInvariant()));
            var share = "RemotePrint" + Convert.ToHexString(hash.AsSpan(0, 8));
            // Persist intent first so a crash can be reconciled on restart.
            config.ManagedWindowsShares[printer] = share;
            save();
            access.SetShare(printer, null, share);
            current[printer] = share;
            changed = true;
        }
        return changed;
    }
}
