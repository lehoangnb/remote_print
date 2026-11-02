using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace RemotePrint.Client;

internal static class AdminAgentStartup
{
    private const string PreferencePath = @"Software\RemotePrint";
    private const string PreferenceName = "RunAsAdministrator";
    internal static string CurrentSid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }
    internal static string TaskName(string sid) => "RemotePrint.Client." + new SecurityIdentifier(sid).Value;

    internal static bool IsEnabled(string sid)
    {
        using var key = Registry.Users.OpenSubKey($@"{sid}\{PreferencePath}");
        return key?.GetValue(PreferenceName) is int value && value == 1;
    }

    internal static string Configure(string sid, bool enabled)
    {
        SetupPaths.RequireAdministrator("đổi chế độ chạy client");
        if (enabled && sid != CurrentSid)
            throw new InvalidOperationException("Chạy admin khi đăng nhập cần tài khoản Windows hiện tại thuộc nhóm Administrators. Không thể dùng quyền của tài khoản khác cho tùy chọn này.");
        var service = new ServiceRegistrar().CurrentBinaryPath();
        var executable = service is null ? "" : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(service))!, "RemotePrint.Client.exe");
        var root = Path.GetFullPath(SetupPaths.InstallRoot) + Path.DirectorySeparatorChar;
        if (!File.Exists(executable) || !Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hãy cài dịch vụ trước khi đổi chế độ chạy client.");
        using var key = Registry.Users.CreateSubKey($@"{sid}\{PreferencePath}")
            ?? throw new InvalidOperationException("Không lưu được chế độ chạy client.");
        var previous = IsEnabled(sid);
        try
        {
            if (enabled)
            {
                Register(sid, executable);
                DesktopAgentStartup.Unregister(sid);
            }
            else
            {
                DesktopAgentStartup.Register(sid, executable);
                Delete(sid);
            }
            key.SetValue(PreferenceName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Restore the previously selected startup path if either registration fails.
            if (previous) { Register(sid, executable); DesktopAgentStartup.Unregister(sid); }
            else { DesktopAgentStartup.Register(sid, executable); Delete(sid); }
            throw;
        }
        return executable;
    }

    internal static string Definition(string sid, string executable)
    {
        _ = new SecurityIdentifier(sid);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("Triggers", E("LogonTrigger", new[] { E("Enabled", "true"), E("UserId", sid) })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "Client"),
                E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "Parallel"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("ExecutionTimeLimit", "PT0S") }),
            new XElement(ns + "Actions", new XAttribute("Context", "Client"),
                E("Exec", new[] { E("Command", executable), E("Arguments", "--background --admin-startup $(Arg0)"),
                    E("WorkingDirectory", Path.GetDirectoryName(executable)!) }))).ToString();
    }

    internal static void Register(string sid, string executable) => WithFolder(folder =>
    {
        object task = folder.RegisterTask(TaskName(sid), Definition(sid, executable), 6, sid, null, 3, null);
        Release(task);
    });

    internal static void Delete(string sid) => WithFolder(folder =>
    {
        try { folder.DeleteTask(TaskName(sid), 0); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
    });

    internal static void Run(bool showManager) => WithFolder(folder =>
    {
        object task = folder.GetTask(TaskName(CurrentSid));
        try
        {
            if (!((dynamic)task).Enabled) throw new InvalidOperationException("Không khởi động được client với quyền quản trị.");
            object running = ((dynamic)task).Run(showManager ? "--show-manager" : "");
            Release(running);
        }
        finally { Release(task); }
    });

    private static void WithFolder(Action<dynamic> action)
    {
        object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Không mở được Task Scheduler."))!;
        object? folder = null;
        try
        {
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            action(folder);
        }
        finally { Release(folder); Release(service); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
