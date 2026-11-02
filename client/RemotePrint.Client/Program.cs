using System.Drawing;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace RemotePrint.Client;

internal static class Program
{
    internal static readonly Icon AppIcon = LoadAppIcon();

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("RemotePrint.Client.remote-print.ico")
            ?? throw new InvalidOperationException("Remote Print icon resource is missing.");
        return new Icon(stream);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // UAC elevation keeps the same user SID, so this check has to run before the
        // single-instance mutex: otherwise the elevated copy would just raise the open
        // manager window and the installation would silently do nothing.
        if (ElevatedSetup.IsRequested(args)) return ElevatedSetup.RunChild(args);
        var waitIndex = Array.IndexOf(args, "--wait-for-exit");
        if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out var previousId))
        {
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(previousId);
                if (!previous.WaitForExit(30000)) return 5;
            }
            catch (ArgumentException) { }
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Segoe UI", 9F));
        if (args.Contains("--uninstall")) return ClientUninstaller.RunStandalone();

        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var showManager = !args.Contains("--background") || args.Contains("--show-manager");
        if (!SetupPaths.IsAdministrator && !args.Contains("--admin-startup") && AdminAgentStartup.IsEnabled(sid))
        {
            try { AdminAgentStartup.Run(showManager); return 0; }
            catch (Exception ex)
            {
                // A missing/disabled task must still let the user open Settings and repair the choice.
                if (showManager) MessageBox.Show(L.T("Không khởi động được client với quyền quản trị.") + "\n" + L.Message(ex.Message),
                    "Remote Print", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        Mutex? instance = null;
        EventWaitHandle showEvent;
        EventWaitHandle exitEvent;
        bool isFirst;
        try
        {
            instance = new Mutex(true, $"Local\\RemotePrint.UI.{sid}", out isFirst);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\RemotePrint.Show.{sid}");
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName(sid));
        }
        catch (UnauthorizedAccessException)
        {
            instance?.Dispose();
            if (showManager) MessageBox.Show(L.T("Client đang chạy với quyền khác. Hãy mở từ khay hệ thống hoặc thoát client rồi mở lại."),
                "Remote Print", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 6;
        }
        using var instanceLifetime = instance;
        using var showEventLifetime = showEvent;
        using var exitEventLifetime = exitEvent;
        if (!isFirst)
        {
            if (showManager) showEvent.Set();
            return 0;
        }

        var form = new MainForm();
        // A tray-only instance also needs a handle so later launches can post ShowManager.
        _ = form.Handle;
        // The window only collects to the tray, so the process must outlive the form.
        using var context = new ApplicationContext();
        using var tray = new NotifyIcon
        {
            Icon = AppIcon,
            Text = L.T("Remote Print · quản lý máy in"),
            Visible = true
        };
        tray.ContextMenuStrip = new ContextMenuStrip();
        var openItem = tray.ContextMenuStrip.Items.Add(L.T("Mở Remote Print"), null, (_, _) => ShowManager());
        var exitItem = tray.ContextMenuStrip.Items.Add(L.T("Thoát giao diện (service vẫn chạy)"), null, (_, _) => ExitInterface());
        form.LanguageChanged += (_, _) =>
        {
            tray.Text = L.T("Remote Print · quản lý máy in");
            openItem.Text = L.T("Mở Remote Print");
            exitItem.Text = L.T("Thoát giao diện (service vẫn chạy)");
        };
        tray.DoubleClick += (_, _) => ShowManager();

        void ShowManager()
        {
            if (form.IsDisposed) return;
            form.Show();
            form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        void ExitInterface()
        {
            form.StopAgent();
            Application.Exit();
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    if (WaitHandle.WaitAny([showEvent, exitEvent]) == 1)
                    {
                        if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(ExitInterface);
                        break;
                    }
                    if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(ShowManager);
                }
            }
            catch (ObjectDisposedException) { }
        });

        if (showManager) ShowManager();
        // Background mode never raises Load, but must still poll the service.
        form.StartAgent();
        Application.Run(context);
        form.StopAgent();
        return 0;
    }

    private static string ExitEventName(string sid) => $"Local\\RemotePrint.Exit.{sid}";

    internal static void RequestClientExit()
    {
        try
        {
            using var exit = EventWaitHandle.OpenExisting(ExitEventName(WindowsIdentity.GetCurrent().User!.Value));
            exit.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }
}
