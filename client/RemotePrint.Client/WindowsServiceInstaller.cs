using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace RemotePrint.Client;

// Registration work is behind this interface so the Checks suite can run the whole
// install flow against a fake and assert the order the service control manager is
// driven in without touching a real machine.
internal interface IServiceRegistrar
{
    bool Exists();
    string? CurrentBinaryPath();

    /// <summary>Creates the service, or points an existing one at a new binary, then
    /// re-applies the description, delayed auto start and restart policy.</summary>
    void Configure(string binaryPath, bool update);

    void Stop();
    void Start();
    void Remove();
}

internal sealed class ServiceRegistrar : IServiceRegistrar
{
    internal const uint ConfigureAccess = NativeWindowsService.AccessQueryConfig |
        NativeWindowsService.AccessChangeConfig | NativeWindowsService.AccessQueryStatus | NativeWindowsService.AccessStart;
    internal const uint RemoveAccess = NativeWindowsService.AccessDelete;

    public bool Exists()
    {
        var manager = NativeWindowsService.OpenManager(NativeWindowsService.ManagerConnect);
        try
        {
            var service = NativeWindowsService.TryOpen(manager, SetupPaths.ServiceName, ConfigureAccess);
            if (service == IntPtr.Zero) return false;
            NativeWindowsService.CloseServiceHandle(service);
            return true;
        }
        finally { NativeWindowsService.CloseServiceHandle(manager); }
    }

    public string? CurrentBinaryPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + SetupPaths.ServiceName);
        return (key?.GetValue("ImagePath") as string)?.Trim().Trim('"');
    }

    public void Configure(string binaryPath, bool update)
    {
        var manager = NativeWindowsService.OpenManager(update
            ? NativeWindowsService.ManagerConnect
            : NativeWindowsService.ManagerConnect | NativeWindowsService.ManagerCreateService);
        try
        {
            var handle = update
                ? NativeWindowsService.Open(manager, SetupPaths.ServiceName, ConfigureAccess)
                : NativeWindowsService.Create(manager, SetupPaths.ServiceName, SetupPaths.ServiceDisplayName,
                    binaryPath, ["Spooler"]);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException($"Không mở được dịch vụ {SetupPaths.ServiceName}.");
            try
            {
                if (update) NativeWindowsService.UpdateBinaryPath(handle, binaryPath);
                NativeWindowsService.SetDescription(handle, SetupPaths.ServiceDescription);
                NativeWindowsService.SetDelayedAutoStart(handle, delayed: true);
                NativeWindowsService.SetRestartPolicy(handle, [5000, 15000, 60000]);
            }
            finally { NativeWindowsService.CloseServiceHandle(handle); }
        }
        finally { NativeWindowsService.CloseServiceHandle(manager); }
    }

    public void Stop() => WithService(NativeWindowsService.AccessStop | NativeWindowsService.AccessQueryStatus, service =>
    {
        NativeWindowsService.Stop(service);
        NativeWindowsService.WaitForState(service, NativeWindowsService.Stopped,
            TimeSpan.FromSeconds(30), "đã dừng");
    });

    public void Start() => WithService(NativeWindowsService.AccessStart | NativeWindowsService.AccessQueryStatus, service =>
    {
        NativeWindowsService.Start(service);
        NativeWindowsService.WaitForState(service, NativeWindowsService.Running,
            TimeSpan.FromSeconds(30), "đang chạy");
    });

    public void Remove() => WithService(RemoveAccess, NativeWindowsService.Delete);

    private static void WithService(uint access, Action<IntPtr> work)
    {
        var manager = NativeWindowsService.OpenManager(NativeWindowsService.ManagerConnect);
        try
        {
            var service = NativeWindowsService.Open(manager, SetupPaths.ServiceName, access);
            try { work(service); }
            finally { NativeWindowsService.CloseServiceHandle(service); }
        }
        finally { NativeWindowsService.CloseServiceHandle(manager); }
    }
}

/// <param name="guard">The Checks suite injects a fake so the whole flow runs unelevated
/// against temporary folders; every production caller leaves it on the real gate.</param>
internal sealed class WindowsServiceInstaller(IServiceRegistrar registrar, SetupRoots? roots = null,
    IAgentStartup? agent = null, string? packageRoot = null, ISetupGuard? guard = null)
{
    private SetupRoots Roots => roots ?? SetupRoots.Default;
    private IAgentStartup Agent => agent ?? new RegistryAgentStartup();
    private ISetupGuard Guard => guard ?? new WindowsSetupGuard();

    // The elevated worker installs the copy of the package it was launched from, which is
    // always the running executable's own folder.
    public string PackageRoot => packageRoot ?? SetupPaths.PackageRoot;

    public void Install(string userSid, string? migrationFile)
    {
        Guard.RequireAdministrator("cài dịch vụ Remote Print");
        if (!File.Exists(SetupPaths.ServiceExecutable(PackageRoot)))
            throw new InvalidOperationException(
                "Thiếu RemotePrint.Service.exe. Hãy chạy tệp cài đặt từ bản phát hành đầy đủ.");
        try
        {
            Directory.CreateDirectory(Roots.ServiceData);
            Guard.RestrictToAdministrators(Roots.ServiceData);
            var version = Roots.NewVersionDirectory();
            SetupPaths.CopyPackage(PackageRoot, version);
            var config = Path.Combine(Roots.ServiceData, "config.json");
            if (migrationFile is not null && !File.Exists(config)) File.Copy(migrationFile, config);
            File.WriteAllText(Path.Combine(Roots.ServiceData, "access.json"),
                JsonSerializer.Serialize(new { UserSid = userSid }));
            var update = registrar.Exists();
            var previousBinary = update ? registrar.CurrentBinaryPath() : null;
            if (update) registrar.Stop();
            registrar.Configure(SetupPaths.ServiceExecutable(version), update: update);
            Agent.Register(userSid, Path.Combine(version, "RemotePrint.Client.exe"));
            registrar.Start();
            Roots.WriteLog($"Installed successfully: {DateTime.Now:O}");
            if (update && previousBinary is not null)
            {
                try { PruneVersions(version, previousBinary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    Roots.WriteLog($"Installed successfully; old versions not fully removed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            try { Roots.WriteLog(ex.ToString()); } catch (IOException) { }
            throw;
        }
    }

    private void PruneVersions(string current, string previousBinary)
    {
        var versions = Path.GetFullPath(Path.Combine(Roots.InstallRoot, "versions"));
        if ((File.GetAttributes(versions) & FileAttributes.ReparsePoint) != 0) return;
        var currentFull = Path.GetFullPath(current);
        if (!string.Equals(Path.GetDirectoryName(currentFull), versions, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(currentFull), "N", out _) ||
            (File.GetAttributes(currentFull) & FileAttributes.ReparsePoint) != 0) return;
        var previousService = Path.GetFullPath(previousBinary);
        var previousDir = Path.GetDirectoryName(Path.GetDirectoryName(previousService));
        if (previousDir is null || !string.Equals(Path.GetDirectoryName(previousDir), versions, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(previousDir), "N", out _) ||
            !string.Equals(previousService, SetupPaths.ServiceExecutable(previousDir), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(previousService) || (File.GetAttributes(previousDir) & FileAttributes.ReparsePoint) != 0) return;
        var source = Path.GetFullPath(PackageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var failures = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(versions))
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (!string.Equals(Path.GetDirectoryName(full), versions, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(Path.GetFileName(full), "N", out _) ||
                    string.Equals(full, currentFull, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, previousDir, StringComparison.OrdinalIgnoreCase) ||
                    source.Equals(full, StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
                // Refuse to traverse junctions or symlinks inside a version directory.
                if (ContainsReparsePoint(full)) continue;
                Directory.Delete(full, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (failures.Count > 0) throw new IOException(string.Join("; ", failures));
    }

    private static bool ContainsReparsePoint(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            if ((attributes & FileAttributes.Directory) != 0 && ContainsReparsePoint(entry)) return true;
        }
        return false;
    }

    public void Uninstall()
    {
        Guard.RequireAdministrator("gỡ dịch vụ Remote Print");
        Agent.Unregister(WindowsIdentity.GetCurrent().User!.Value);
        if (!registrar.Exists()) return;
        registrar.Stop();
        registrar.Remove();
    }
}
