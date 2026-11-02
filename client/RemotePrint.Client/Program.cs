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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetDefaultFont(new Font("Segoe UI", 9F));

        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using var instance = new Mutex(true, $"Local\\RemotePrint.UI.{sid}", out var isFirst);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\RemotePrint.Show.{sid}");
        if (!isFirst)
        {
            showEvent.Set();
            return 0;
        }

        var form = new MainForm();
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
                while (showEvent.WaitOne())
                {
                    if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(ShowManager);
                }
            }
            catch (ObjectDisposedException) { }
        });

        if (!args.Contains("--background")) ShowManager();
        Application.Run(context);
        form.StopAgent();
        return 0;
    }
}
