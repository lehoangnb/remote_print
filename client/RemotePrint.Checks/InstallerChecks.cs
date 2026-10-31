using System.IO;
using System.Security.Principal;
using RemotePrint.Client;

internal static partial class Program
{
    private const string TestSid = "S-1-5-21-4000000000-4000000000-4000000000-1001";
    private static void RunInstallerCheck(string workingDirectory)
    {
        var root = Path.Combine(workingDirectory, "artifacts", "installer-check-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(package, "Service"));
        File.WriteAllText(Path.Combine(package, "Service", "RemotePrint.Service.exe"), "fake service");
        File.WriteAllText(Path.Combine(package, "RemotePrint.Client.exe"), "fake client");
        CheckServiceInterop();
        CheckElevatedResults();
        CheckServiceInstaller(Path.Combine(root, "service"), package);
        var denied = new FakeGuard { IsAdministrator = false };
        var registrar = new FakeRegistrar();
        var service = new WindowsServiceInstaller(registrar, new SetupRoots(Path.Combine(root,"denied-install"),Path.Combine(root,"denied-data")), new FakeAgent(), package, denied);
        Require(Throws(() => service.Install(TestSid, null), "Cần quyền quản trị"), "Installation requires administrator rights");
        Require(Throws(service.Uninstall, "Cần quyền quản trị"), "Removal requires administrator rights");
        Require(registrar.Calls.Count == 0, "Rejected setup makes no service changes");
    }
    private static void CheckServiceInterop()
    {
        Require((ServiceRegistrar.ConfigureAccess & NativeWindowsService.AccessStart) != 0 &&
                NativeWindowsService.ConfigFailureActionsFlag == 4,
            "Service recovery configuration requests START access and the correct Windows info level");
        Require((ServiceRegistrar.RemoveAccess & NativeWindowsService.AccessDelete) != 0,
            "Removing a service requests DELETE access");
        // A query-only handle cannot change the service, but exercises the real
        // native RPC marshalling that previously crashed the elevated installer.
        var manager = NativeWindowsService.OpenManager(NativeWindowsService.ManagerConnect);
        try
        {
            var service = NativeWindowsService.Open(manager, "Spooler", NativeWindowsService.AccessQueryStatus);
            try
            {
                foreach (var call in new System.Action[] {
                    () => NativeWindowsService.SetDescription(service, "Remote Print interop check"),
                    () => NativeWindowsService.SetRestartPolicy(service, [5000, 15000, 60000]) })
                {
                    try { call(); throw new InvalidOperationException("Query-only service handle changed configuration"); }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) { }
                }
                Require(true, "Service configuration calls return access denied without crashing");
            }
            finally { NativeWindowsService.CloseServiceHandle(service); }
        }
        finally { NativeWindowsService.CloseServiceHandle(manager); }
    }

    private static void CheckElevatedResults()
    {
        Require(ElevatedSetup.ParseResult("{\"ok\":true,\"message\":null}") is null,
            "Successful elevated result is accepted");
        Require(ElevatedSetup.ParseResult("{\"ok\":false,\"message\":\"Installer failure\"}") == "Installer failure",
            "Elevated installer error is preserved");
        foreach (var json in new[] { "", "[]", "{}", "{\"ok\":\"true\"}", "{\"ok\":false}", "{\"ok\":false,\"message\":null}" })
            Require(ElevatedSetup.ParseResult(json) is not null, "Malformed or incomplete elevated result fails closed");
        Require(ElevatedSetup.RunChild(["--setup", "999", "--result", "bad"]) == 2,
            "Unknown elevated operation is rejected");
        Require(ElevatedSetup.RunChild(["--setup", "InstallService", "--result", "bad"]) == 3,
            "Unsafe elevated result path is rejected");
    }

    private static void CheckServiceInstaller(string root, string package)
    {
        var roots = new SetupRoots(Path.Combine(root, "ProgramFiles"), Path.Combine(root, "ProgramData"));
        var guard = new FakeGuard();
        var registrar = new FakeRegistrar();
        var agent = new FakeAgent();
        var installer = new WindowsServiceInstaller(registrar, roots, agent, package, guard);
        var config = Path.Combine(roots.ServiceData, "config.json");

        installer.Install(TestSid, null);
        Require(registrar.Calls is ["create", "start"], "Fresh install creates the service and then starts it");
        Require(guard.Restricted.SequenceEqual([roots.ServiceData]),
            "Service data folder is locked down before anything is written");
        var version = Path.GetDirectoryName(Path.GetDirectoryName(registrar.Configured[0].BinaryPath))!;
        Require(version.StartsWith(Path.Combine(roots.InstallRoot, "versions"), StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(version, "RemotePrint.Client.exe")) &&
                File.Exists(SetupPaths.ServiceExecutable(version)),
            "Each install gets its own complete copy of the package");
        Require(File.ReadAllText(Path.Combine(roots.ServiceData, "access.json")).Contains(TestSid, StringComparison.Ordinal),
            "access.json authorises the user who clicked install");
        Require(!File.Exists(config), "Install without a migration file leaves the service unconfigured");
        Require(agent.RegisteredSid == TestSid &&
                agent.RegisteredPath == Path.Combine(version, "RemotePrint.Client.exe"),
            "Desktop agent runs from the installed version through the Run key");
        Require(File.ReadAllText(roots.LogPath).Contains("Installed successfully", StringComparison.Ordinal),
            "Install records the result in install.log");

        File.WriteAllText(config, "existing-config");
        var migration = Path.Combine(root, "service-migration-check.json");
        File.WriteAllText(migration, "migrated-config");
        registrar.ExistsFlag = true;
        registrar.Calls.Clear();
        installer.Install(TestSid, migration);
        Require(registrar.Calls.SequenceEqual(new[] { "stop", "update", "start" }),
            "Updating a running service restarts it with the new binary");
        Require(registrar.Configured[1].Update, "Second install updates the binary path of the existing service");
        Require(File.ReadAllText(config) == "existing-config",
            "A live service configuration is never overwritten by the migration");
        var previousVersion = Path.GetDirectoryName(Path.GetDirectoryName(registrar.Configured[1].BinaryPath))!;
        var unrelatedDirectory = Path.Combine(roots.InstallRoot, "versions", "keep-user-files");
        Directory.CreateDirectory(unrelatedDirectory);
        File.Delete(config);
        installer.Install(TestSid, migration);
        Require(File.ReadAllText(config) == "migrated-config",
            "Migration file becomes the service configuration when none exists");
        var latestVersion = Path.GetDirectoryName(Path.GetDirectoryName(registrar.Configured[2].BinaryPath))!;
        Require(Directory.Exists(previousVersion) && Directory.Exists(latestVersion) && !Directory.Exists(version) &&
                Directory.GetDirectories(Path.Combine(roots.InstallRoot, "versions"))
                    .Count(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)) == 2 &&
                Directory.Exists(unrelatedDirectory),
            "Successful update retains the active version and the previously configured version");
        registrar.FailStart = true;
        Require(Throws(() => installer.Install(TestSid, null), "Simulated start failure") &&
                Directory.Exists(latestVersion) && Directory.Exists(previousVersion),
            "Failed service start preserves existing versions");
        registrar.FailStart = false;

        registrar.Calls.Clear();
        agent.Calls.Clear();
        installer.Uninstall();
        Require(agent.Calls is ["unregister"] && registrar.Calls is ["stop", "remove"],
            "Uninstall clears the Run value, stops the service and then removes it");
        registrar.ExistsFlag = false;
        registrar.Calls.Clear();
        installer.Uninstall();
        Require(registrar.Calls.Count == 0, "Uninstall without the service only clears the Run value");

        var empty = Path.Combine(root, "empty-package");
        Directory.CreateDirectory(empty);
        var broken = new WindowsServiceInstaller(new FakeRegistrar(),
            new SetupRoots(Path.Combine(root, "broken1"), Path.Combine(root, "broken2")),
            new FakeAgent(), empty, new FakeGuard());
        Require(Throws(() => broken.Install(TestSid, null), "Thiếu RemotePrint.Service.exe"),
            "Install refuses a package that has no service executable");
    }

    private static bool Throws(Action action, string expectedFragment)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            return ex.Message.Contains(expectedFragment, StringComparison.Ordinal);
        }
    }

    private static bool DoesNotThrow(Action action)
    {
        try { action(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private sealed class FakeGuard : ISetupGuard
    {
        public readonly List<string> Restricted = [];
        public bool IsAdministrator { get; init; } = true;

        public void RequireAdministrator(string action)
        {
            if (!IsAdministrator) throw new InvalidOperationException($"Cần quyền quản trị để {action}.");
        }

        public void RestrictToAdministrators(string directory) => Restricted.Add(directory);
    }

    private sealed class FakeRegistrar : IServiceRegistrar
    {
        public readonly List<string> Calls = [];
        public readonly List<(string BinaryPath, bool Update)> Configured = [];
        public bool ExistsFlag;
        public bool FailStart;
        private string? _binaryPath;

        public bool Exists() => ExistsFlag;

        public string? CurrentBinaryPath() => _binaryPath;

        public void Configure(string binaryPath, bool update)
        {
            Calls.Add(update ? "update" : "create");
            Configured.Add((binaryPath, update));
            _binaryPath = binaryPath;
        }

        public void Stop() => Calls.Add("stop");

        public void Start()
        {
            Calls.Add("start");
            if (FailStart) throw new InvalidOperationException("Simulated start failure");
        }

        public void Remove() => Calls.Add("remove");
    }

    private sealed class FakeAgent : IAgentStartup
    {
        public readonly List<string> Calls = [];
        public string? RegisteredSid { get; private set; }
        public string? RegisteredPath { get; private set; }

        public void Register(string userSid, string executable)
        {
            Calls.Add("register");
            RegisteredSid = userSid;
            RegisteredPath = executable;
        }

        public void Unregister(string userSid)
        {
            Calls.Add("unregister");
            RegisteredSid = null;
            RegisteredPath = null;
        }
    }
}
