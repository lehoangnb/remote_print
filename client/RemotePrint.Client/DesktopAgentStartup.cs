using Microsoft.Win32;

namespace RemotePrint.Client;

// Normal startup uses the per-user Run key; admin startup uses an interactive logon task.
internal interface IAgentStartup
{
    void Register(string userSid, string executable);
    void Unregister(string userSid);
}

internal interface IStartupRegistration
{
    bool AdminEnabled(string sid);
    void RegisterAdmin(string sid, string executable);
    void RemoveAdmin(string sid);
    void RegisterUser(string sid, string executable);
    void RemoveUser(string sid);
}

internal sealed class StartupRegistration : IStartupRegistration
{
    public bool AdminEnabled(string sid) => AdminAgentStartup.IsEnabled(sid);
    public void RegisterAdmin(string sid, string executable) => AdminAgentStartup.Register(sid, executable);
    public void RemoveAdmin(string sid) => AdminAgentStartup.Delete(sid);
    public void RegisterUser(string sid, string executable) => DesktopAgentStartup.Register(sid, executable);
    public void RemoveUser(string sid) => DesktopAgentStartup.Unregister(sid);
}

internal sealed class RegistryAgentStartup(IStartupRegistration? registration = null) : IAgentStartup
{
    private IStartupRegistration Registration { get; } = registration ?? new StartupRegistration();
    public void Register(string userSid, string executable)
    {
        if (Registration.AdminEnabled(userSid))
        {
            Registration.RegisterAdmin(userSid, executable);
            Registration.RemoveUser(userSid);
        }
        else Registration.RegisterUser(userSid, executable);
    }

    public void Unregister(string userSid)
    {
        Registration.RemoveAdmin(userSid);
        Registration.RemoveUser(userSid);
    }
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

    private static string? Read(string userSid)
    {
        using var key = Registry.Users.OpenSubKey($@"{userSid}\{RunPath}");
        return key?.GetValue(SetupPaths.AgentValueName) as string;
    }

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
