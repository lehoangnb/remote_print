using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using RemotePrint.Client;

internal static partial class Program
{
    private static void CheckAdminStartup(string root)
    {
        CheckServicePresence();
        var release = new ClientRelease("99.0.0", ClientUpdater.Runtime, "", 1, new string('a', 64), "/unused");
        var command = new ClientUpdateRequest("https://print.example.com", release);
        var policy = new ClientUpdatePolicy();
        Require(policy.Decide(command, false, false) == ClientUpdateAction.None,
            "A standard-user tray client neither installs nor prompts");
        policy.GuiOpened();
        Require(policy.Decide(command, false, true) == ClientUpdateAction.Notify &&
            policy.Decide(command, false, true) == ClientUpdateAction.None,
            "Opening the standard-user GUI prompts once for the server-issued release");
        policy.GuiOpened();
        Require(policy.Decide(command, false, true) == ClientUpdateAction.Notify,
            "Reopening the GUI may offer the deferred update again");
        Require(policy.Decide(command, true, false) == ClientUpdateAction.Install &&
            policy.Decide(command, true, true) == ClientUpdateAction.Install,
            "An actually elevated client installs automatically both in the tray and GUI");
        var elevated = ElevatedSetup.StartInfo("client.exe", true);
        var standard = ElevatedSetup.StartInfo("client.exe", false);
        Require(!elevated.UseShellExecute && string.IsNullOrEmpty(elevated.Verb),
            "An elevated update inherits its token instead of invoking runas again");
        Require(standard.UseShellExecute && standard.Verb == "runas", "An accepted standard-user update requests UAC");
        var attempts = new AutomaticUpdateAttempts();
        var now = DateTime.UtcNow;
        Require(attempts.TryBegin(command, now) && !attempts.TryBegin(command, now.AddSeconds(1)) &&
            attempts.TryBegin(command, now.AddSeconds(2), force: true),
            "Admin retries are throttled but an explicit GUI acceptance can retry immediately");

        var xml = AdminAgentStartup.Definition(AdminAgentStartup.CurrentSid, @"C:\Program Files\Remote Print & Test\RemotePrint.Client.exe");
        var definition = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var principal = definition.Descendants(ns + "Principal").Single();
        Require(principal.Element(ns + "UserId")!.Value == AdminAgentStartup.CurrentSid &&
            principal.Element(ns + "RunLevel")!.Value == "HighestAvailable" &&
            principal.Element(ns + "LogonType")!.Value == "InteractiveToken",
            "Admin startup uses the selected user's interactive highest token without storing credentials");
        Require(definition.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value == AdminAgentStartup.CurrentSid,
            "The startup trigger is scoped to the same Windows account");
        Require(definition.Descendants(ns + "Command").Single().Value.Contains("& Test") &&
            definition.Descendants(ns + "Arguments").Single().Value == "--background --admin-startup $(Arg0)" &&
            definition.Descendants(ns + "ExecutionTimeLimit").Single().Value == "PT0S",
            "Task XML escapes paths, starts in the tray, accepts GUI activation and has no runtime expiry");

        object scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try
        {
            ((dynamic)scheduler).Connect(); folder = ((dynamic)scheduler).GetFolder(@"\");
            // TASK_VALIDATE_ONLY performs native XML validation without registering a startup entry.
            object? validated = ((dynamic)folder).RegisterTask(null, xml, 1, null, null, 3, null);
            if (validated is not null && Marshal.IsComObject(validated)) Marshal.FinalReleaseComObject(validated);
            Require(true, "The real Windows Task Scheduler validates the highest-privilege logon definition");
        }
        finally
        {
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(scheduler);
        }

        var backend = new FakeStartupRegistration();
        var startup = new RegistryAgentStartup(backend);
        startup.Register(TestSid, "v1/client.exe");
        Require(backend.Calls.SequenceEqual(["user:v1/client.exe"]), "Normal startup retains the ordinary Run-key path");
        backend.Calls.Clear(); backend.Admin = true;
        startup.Register(TestSid, "v2/client.exe");
        Require(backend.Calls.SequenceEqual(["admin:v2/client.exe", "remove-user"]),
            "Updating an admin client refreshes the task path and removes duplicate normal startup");
        backend.Calls.Clear(); backend.FailAdmin = true;
        Require(Throws(() => startup.Register(TestSid, "v3/client.exe"), "task failure") &&
            !backend.Calls.Contains("remove-user"), "Failed task registration does not remove the fallback startup");
        backend.FailAdmin = false; backend.Calls.Clear(); startup.Unregister(TestSid);
        Require(backend.Calls.SequenceEqual(["remove-admin", "remove-user"]), "Uninstall removes both startup mechanisms");

        var directory = Path.Combine(root, "artifacts", "admin-startup-checks", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(directory, "package");
        Directory.CreateDirectory(Path.Combine(package, "Service"));
        File.WriteAllText(Path.Combine(package, "RemotePrint.Client.exe"), "client");
        File.WriteAllText(Path.Combine(package, "Service", "RemotePrint.Service.exe"), "service");
        var registrar = new FakeRegistrar();
        var installer = new WindowsServiceInstaller(registrar,
            new SetupRoots(Path.Combine(directory, "install"), Path.Combine(directory, "data")), startup, package, new FakeGuard());
        installer.Install(TestSid, null);
        var previousBinary = registrar.CurrentBinaryPath();
        registrar.ExistsFlag = true; registrar.FailNextStarts = 1;
        backend.Calls.Clear();
        Require(Throws(() => installer.Install(TestSid, null), "Simulated start failure") &&
            backend.Calls.Last(call => call.StartsWith("admin:")) ==
                "admin:" + Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(previousBinary!))!, "RemotePrint.Client.exe"),
            "A failed service upgrade restores the admin task to the previous installed client");
        Require(backend.Admin, "Install and rollback preserve the chosen admin startup mode");
        var originalContext = SynchronizationContext.Current;
        using (var form = new MainForm(headless: true))
        {
            Require(form._adminStartupItem.Text == L.T("Luôn chạy với quyền quản trị"), "Settings exposes the admin startup option");
            form.ApplyLanguage("en");
            Require(form._adminStartupItem.Text == "Always run as administrator" &&
                form._adminStartupItem.ToolTipText == "Also applies when starting with Windows.", "Admin startup caption and help switch language immediately");
            form.ApplyLanguage("vi"); form.StopAgent();
        }
        SynchronizationContext.SetSynchronizationContext(originalContext);
    }

    private static void CheckServicePresence()
    {
        Require(ServiceInstallationProbe.FromOpenResult(true, 0) == ServiceInstallationState.Installed &&
            ServiceInstallationProbe.FromOpenResult(false, 1060) == ServiceInstallationState.Missing &&
            ServiceInstallationProbe.FromOpenResult(false, 5) == ServiceInstallationState.Unknown &&
            ServiceInstallationProbe.FromOpenResult(false, 1722) == ServiceInstallationState.Unknown,
            "SCM discovery distinguishes a missing service from pipe outage and SCM access errors");
        Console.WriteLine("Current SCM presence: " + ServiceInstallationProbe.Check());
    }

    private sealed class FakeStartupRegistration : IStartupRegistration
    {
        internal bool Admin, FailAdmin;
        internal readonly List<string> Calls = [];
        public bool AdminEnabled(string sid) => Admin;
        public void RegisterAdmin(string sid, string executable)
        {
            Calls.Add("admin:" + executable);
            if (FailAdmin) throw new InvalidOperationException("task failure");
        }
        public void RemoveAdmin(string sid) => Calls.Add("remove-admin");
        public void RegisterUser(string sid, string executable) => Calls.Add("user:" + executable);
        public void RemoveUser(string sid) => Calls.Add("remove-user");
    }
}
