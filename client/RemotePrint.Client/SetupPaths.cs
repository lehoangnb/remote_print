using System.Security.AccessControl;
using System.Security.Principal;

namespace RemotePrint.Client;

// Everything the installers need that is not specific to the service or the printer:
// the well-known folders, the administrator gate, and the two file operations the
// PowerShell scripts performed (locking a directory down to SYSTEM plus Administrators,
// and copying the package into a fresh version folder).
internal static class SetupPaths
{
    public const string ServiceName = "RemotePrint";
    public const string ServiceDisplayName = "Remote Print Service";
    public const string ServiceDescription =
        "Encrypted remote printing. Runs independently of the desktop UI.";
    public const string AgentValueName = "RemotePrint";

    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemotePrint");

    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemotePrint");

    public static string ServiceData => Path.Combine(DataRoot, "Service");

    public static string LogPath => Path.Combine(DataRoot, "install.log");

    public static string PackageRoot => AppContext.BaseDirectory;

    public static string ServiceExecutable(string versionDirectory) =>
        Path.Combine(versionDirectory, "Service", "RemotePrint.Service.exe");

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static void RequireAdministrator(string action)
    {
        if (!IsAdministrator)
            throw new InvalidOperationException($"Cần quyền quản trị để {action}.");
    }

    // Service configuration is restricted to SYSTEM and Administrators: a desktop
    // user talks to the service over the authenticated pipe, never through the file system.
    public static void RestrictToAdministrators(string directory)
    {
        Directory.CreateDirectory(directory);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var identity in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                 })
        {
            acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(directory).SetAccessControl(acl);
    }

    public static void CopyPackage(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

/// <summary>
/// The two machine-level side effects an installer performs outside the service or
/// spooler APIs. Tests inject a fake so the flow can run unelevated on a standard
/// account, which would otherwise lose access to its own folder once locked down.
/// </summary>
internal interface ISetupGuard
{
    bool IsAdministrator { get; }
    void RequireAdministrator(string action);
    void RestrictToAdministrators(string directory);
}

internal sealed class WindowsSetupGuard : ISetupGuard
{
    public bool IsAdministrator => SetupPaths.IsAdministrator;

    public void RequireAdministrator(string action) => SetupPaths.RequireAdministrator(action);

    public void RestrictToAdministrators(string directory) =>
        SetupPaths.RestrictToAdministrators(directory);
}

/// <summary>
/// The two folders an installation writes to. The real installer uses the machine-wide
/// locations; the Checks suite passes a temporary pair so the copy, ACL and configuration
/// steps can be verified without touching Program Files.
/// </summary>
internal sealed record SetupRoots(string InstallRoot, string DataRoot)
{
    public static SetupRoots Default { get; } = new(SetupPaths.InstallRoot, SetupPaths.DataRoot);

    public string ServiceData => Path.Combine(DataRoot, "Service");
    public string LogPath => Path.Combine(DataRoot, "install.log");

    public string NewVersionDirectory()
    {
        // Each install gets its own folder so a running UI never holds a file the
        // service is about to be restarted from.
        var path = Path.Combine(InstallRoot, "versions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void WriteLog(string text)
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(LogPath, text);
    }
}
