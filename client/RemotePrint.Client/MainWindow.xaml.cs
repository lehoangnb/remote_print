using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace RemotePrint.Client;

public partial class MainWindow : Window
{
    private readonly ServiceClient _service = new();
    private readonly ObservableCollection<PrinterChoice> _printers = [];
    private readonly VirtualPrinterService _virtualPrinter = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<string> _deferred = [];
    private ServiceSnapshot? _snapshot;
    private bool _refreshing, _settingsLoaded;

    public MainWindow()
    {
        InitializeComponent();
        PrintersList.ItemsSource = _printers;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        _ = RefreshAsync();
        UpdateVirtualPrinterStatus();
    }

    public void StopAgent() => _timer.Stop();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            ApplySnapshot(await _service.CallAsync(new("status")));
            if (_snapshot!.State.Status == "approved" && _snapshot.Connection == "Đã kết nối")
            {
                var document = _snapshot.Pending.FirstOrDefault(item => !_deferred.Contains(item.Id));
                if (document is not null) await RouteDocumentAsync(document);
            }
        }
        catch (Exception ex)
        {
            ServiceStatusText.Text = "Chưa chạy";
            StatusText.Text = "Chưa kết nối dịch vụ";
            ConnectButton.IsEnabled = false;
            ServiceInstallButton.Content = "Cài / khởi động dịch vụ";
            FooterText.Text = $"Cần cài Windows Service để nhận lệnh in. {ex.Message}";
            _snapshot = null;
            PeersGrid.ItemsSource = null;
            EmptyPeersPanel.Visibility = Visibility.Visible;
            if (!_settingsLoaded)
            {
                var old = new ConfigService().LoadOrCreate();
                ComputerNameBox.Text = old.ComputerName;
                ServerUrlBox.Text = old.ServerUrl;
                _settingsLoaded = true;
            }
        }
        finally { _refreshing = false; }
    }

    private void ApplySnapshot(ServiceSnapshot snapshot)
    {
        _snapshot = snapshot;
        ServiceStatusText.Text = "● Đang chạy";
        StatusText.Text = snapshot.Connection;
        RoomText.Text = snapshot.State.Room ?? "Chưa được gán";
        ConnectButton.IsEnabled = true;
        ServiceInstallButton.Content = "Cập nhật dịch vụ";
        FooterText.Text = snapshot.Pending.Count > 0
            ? $"Có {snapshot.Pending.Count} tài liệu chờ chọn máy nhận. Dịch vụ vẫn giữ tài liệu khi đóng giao diện."
            : "Đóng cửa sổ để thu về khay hệ thống. Windows Service tiếp tục nhận và in tài liệu.";
        if (!_settingsLoaded)
        {
            ComputerNameBox.Text = snapshot.Settings.ComputerName;
            ServerUrlBox.Text = snapshot.Settings.ServerUrl;
            _settingsLoaded = true;
        }
        var selected = _printers.Count == 0 ? snapshot.Settings.SharedPrinters.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : _printers.Where(p => p.IsSelected).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_printers.Select(p => p.Name).SequenceEqual(snapshot.Printers))
        {
            _printers.Clear();
            foreach (var name in snapshot.Printers) _printers.Add(new() { Name = name, IsSelected = selected.Contains(name) });
        }
        PrintersHintText.Text = snapshot.Printers.Count == 0
            ? "Dịch vụ chưa thấy máy in. Cài máy in cho toàn bộ máy tính rồi quét lại."
            : $"{snapshot.Printers.Count} máy in khả dụng cho dịch vụ.";
        PeersGrid.ItemsSource = snapshot.Peers;
        EmptyPeersPanel.Visibility = snapshot.Peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeersSummaryText.Text = $"{snapshot.Peers.Count} máy online · chỉ hiển thị trong cùng Room";
        ActivityList.ItemsSource = snapshot.Activity.Select(item => $"{item.At:HH:mm:ss}  {item.Message}");
    }

    private async Task RouteDocumentAsync(PendingDocument document)
    {
        var dialog = new RoutePrintWindow(_snapshot!.Peers, document.Id + ".pdf", async () =>
        {
            var snapshot = await _service.CallAsync(new("status"));
            return snapshot.Peers;
        });
        if (IsVisible) dialog.Owner = this;
        if (dialog.ShowDialog() != true || dialog.TargetClient is null || dialog.PrinterName is null)
        {
            _deferred.Add(document.Id);
            FooterText.Text = "Tài liệu đã được giữ lại. Bấm Làm mới để chọn máy nhận sau.";
            return;
        }
        try
        {
            ApplySnapshot(await _service.CallAsync(new("send", DocumentId: document.Id,
                TargetId: dialog.TargetClient.Id, PrinterName: dialog.PrinterName)));
        }
        catch (Exception ex)
        {
            _deferred.Add(document.Id);
            System.Windows.MessageBox.Show($"{ex.Message}\nTài liệu vẫn được giữ. Bấm Làm mới để thử lại.", "Gửi chưa thành công",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new DeviceSettings(ComputerNameBox.Text, ServerUrlBox.Text,
                _printers.Where(item => item.IsSelected).Select(item => item.Name).ToList());
            ApplySnapshot(await _service.CallAsync(new("configure", settings)));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    { _deferred.Clear(); await RefreshAsync(); }

    private async void ScanPrintersButton_Click(object sender, RoutedEventArgs e)
    {
        try { await _service.CallAsync(new("refresh")); await Task.Delay(2200); await RefreshAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void InstallService_Click(object sender, RoutedEventArgs e)
    {
        ServiceInstallButton.IsEnabled = false;
        string? migration = null;
        try
        {
            migration = new ConfigService().PrepareServiceMigration();
            var script = Path.Combine(AppContext.BaseDirectory, "Scripts", "Install-Service.ps1");
            var start = new ProcessStartInfo("powershell.exe")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                "-PackageDirectory", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                "-UserSid", WindowsIdentity.GetCurrent().User!.Value, "-MigrationFile", migration }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Không mở được trình cài dịch vụ.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Cài service thất bại (mã {process.ExitCode}). Xem install.log trong ProgramData\\RemotePrint.");
            _settingsLoaded = false;
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (migration is not null) File.Delete(migration); ServiceInstallButton.IsEnabled = true; }
    }

    private async void InstallVirtualPrinter_Click(object sender, RoutedEventArgs e)
    {
        try { await _virtualPrinter.InstallAsync(); UpdateVirtualPrinterStatus(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void UninstallVirtualPrinter_Click(object sender, RoutedEventArgs e)
    {
        try { await _virtualPrinter.UninstallAsync(); UpdateVirtualPrinterStatus(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateVirtualPrinterStatus() => VirtualPrinterStatusText.Text = _virtualPrinter.IsInstalled()
        ? "● Remote Print đã cài · sẵn sàng nhận lệnh" : "Chưa cài máy in ảo Remote Print trên Windows";

    private static void ShowError(Exception ex) => System.Windows.MessageBox.Show(ex.Message, "Remote Print",
        MessageBoxButton.OK, MessageBoxImage.Warning);
}
