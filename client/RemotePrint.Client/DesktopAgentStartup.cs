using Microsoft.Win32;

namespace RemotePrint.Client;

// The desktop agent used to be started by a scheduled task registered from an elevated
// installer. A per-user Run value does the same job with no task service, and the agent
// that is already running when the user clicks Install keeps running.
internal interface IAgentStartup
{
    void Register(string userSid, string executable);
    void Unregister(string userSid);
}

internal sealed class RegistryAgentStartup : IAgentStartup
{
    public void Register(string userSid, string executable) => DesktopAgentStartup.Register(userSid, executable);

    public void Unregister(string userSid) => DesktopAgentStartup.Unregister(userSid);
}

internal static class DesktopAgentStartup
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsRegistered(string userSid) => Read(userSid) is not null;

    public static void Register(string userSid, string executable)
    {
        using var key = Open(userSid, writable: true);
        key.SetValue(SetupPaths.AgentValueName, $"\"{executable}\" --background");
    }

    public static void Unregister(string userSid)
    {
        using var key = Open(userSid, writable: true);
        if (key.GetValue(SetupPaths.AgentValueName) is not null) key.DeleteValue(SetupPaths.AgentValueName);
    }

    private static string? Read(string userSid) =>
        Registry.Users.OpenSubKey($@"{userSid}\{RunPath}")?.GetValue(SetupPaths.AgentValueName) as string;

    private static RegistryKey Open(string userSid, bool writable)
    {
        // HKEY_USERS only contains a hive once that user has a session, so a missing key
        // means the account is not signed in rather than that the value is absent.
        using var hive = Registry.Users.OpenSubKey(userSid, writable: true)
            ?? throw new InvalidOperationException(
                "Tài khoản này chưa đăng nhập nên không đăng ký khởi động cùng Windows được.");
        return hive.CreateSubKey(RunPath, writable)
            ?? throw new InvalidOperationException("Không mở được khóa khởi động cùng Windows.");
    }
}
