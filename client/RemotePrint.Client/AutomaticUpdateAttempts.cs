namespace RemotePrint.Client;

// Repeated API replies must not launch overlapping installations or loop on a failed/canceled install.
internal sealed class AutomaticUpdateAttempts
{
    private readonly Dictionary<string, DateTime> _retryAt = new();

    internal bool TryBegin(ClientUpdateRequest command, DateTime now, bool force = false)
    {
        var identity = command.ServerUrl + "/" + command.Release.Sha256 + "/" + command.Release.ReleaseId;
        foreach (var expired in _retryAt.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
            _retryAt.Remove(expired);
        if (!force && _retryAt.ContainsKey(identity)) return false;
        _retryAt[identity] = now.AddMinutes(15);
        return true;
    }
}
