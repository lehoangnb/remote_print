using System.Security.Principal;
using System.Windows;
using Forms = System.Windows.Forms;

namespace RemotePrint.Client;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        _instance = new Mutex(true, $"Local\\RemotePrint.UI.{sid}", out var created);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\RemotePrint.Show.{sid}");
        if (!created) { _showEvent.Set(); Shutdown(); return; }
        MainWindow = new MainWindow();
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application, Text = "Remote Print · quản lý máy in", Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.DoubleClick += (_, _) => ShowManager();
        _tray.ContextMenuStrip.Items.Add("Mở Remote Print", null, (_, _) => ShowManager());
        _tray.ContextMenuStrip.Items.Add("Thoát giao diện (service vẫn chạy)", null, (_, _) => Shutdown());
        _ = Task.Run(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowManager);
        });
        if (!e.Args.Contains("--background")) MainWindow.Show();
    }

    private void ShowManager()
    {
        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.StopAgent();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
