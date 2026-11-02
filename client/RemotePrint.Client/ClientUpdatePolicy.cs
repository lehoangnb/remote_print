namespace RemotePrint.Client;

internal enum ClientUpdateAction { None, Install, Notify }

internal sealed class ClientUpdatePolicy
{
    private readonly HashSet<string> _notified = [];
    internal void GuiOpened() => _notified.Clear();

    internal ClientUpdateAction Decide(ClientUpdateRequest command, bool administrator, bool guiVisible)
    {
        if (administrator) return ClientUpdateAction.Install;
        if (!guiVisible) return ClientUpdateAction.None;
        return _notified.Add(command.ServerUrl + "/" + command.Release.Sha256)
            ? ClientUpdateAction.Notify : ClientUpdateAction.None;
    }
}
