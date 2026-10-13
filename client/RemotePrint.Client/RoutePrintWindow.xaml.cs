using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RemotePrint.Client;

public partial class RoutePrintWindow : Window
{
    private readonly Func<Task<IReadOnlyList<PeerClient>>>? _refreshPeers;

    public PeerClient? TargetClient => ComputerList.SelectedItem as PeerClient;
    public string? PrinterName => PrinterList.SelectedItem as string;

    public RoutePrintWindow(
        IReadOnlyList<PeerClient> peers,
        string documentPath,
        Func<Task<IReadOnlyList<PeerClient>>>? refreshPeers = null)
    {
        InitializeComponent();
        _refreshPeers = refreshPeers;
        DocumentText.Text = "Tài liệu vừa gửi từ máy in ảo Remote Print";
        ToolTipService.SetToolTip(DocumentText, Path.GetFileName(documentPath));
        SetPeers(peers);
    }

    private void SetPeers(IReadOnlyList<PeerClient> peers)
    {
        var selectedClientId = TargetClient?.Id;
        var availablePeers = peers
            .Where(peer => peer.Printers.Count > 0)
            .OrderBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ComputerList.ItemsSource = availablePeers;
        ClientCountText.Text = $"{availablePeers.Count} máy";
        EmptyClientsPanel.Visibility = availablePeers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ComputerList.SelectedItem = availablePeers.FirstOrDefault(peer => peer.Id == selectedClientId);
        if (ComputerList.SelectedItem is null && availablePeers.Count > 0)
            ComputerList.SelectedIndex = 0;

        if (availablePeers.Count == 0)
        {
            PrinterList.ItemsSource = null;
            EmptyPrintersPanel.Visibility = Visibility.Visible;
            PrinterHintText.Text = "Chưa có máy in từ xa để lựa chọn.";
            StatusText.Text = "Không tìm thấy máy khác trong Room";
        }

        UpdateSendButton();
    }

    private void ComputerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var printers = TargetClient?.Printers
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList() ?? [];

        PrinterList.ItemsSource = printers;
        PrinterList.SelectedIndex = printers.Count > 0 ? 0 : -1;
        EmptyPrintersPanel.Visibility = printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PrinterHintText.Text = TargetClient is null
            ? "Chọn một máy ở bên trái để xem máy in."
            : "Máy này chưa chia sẻ máy in nào.";
        StatusText.Text = TargetClient is null ? "" : $"Đã chọn {TargetClient.Name}";
        UpdateSendButton();
    }

    private void PrinterList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSendButton();

    private void UpdateSendButton() => SendButton.IsEnabled = TargetClient is not null && PrinterName is not null;

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshPeers is null) return;

        RefreshButton.IsEnabled = false;
        StatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 112, 133));
        StatusText.Text = "Đang cập nhật danh sách…";
        try
        {
            SetPeers(await _refreshPeers());
            if (ComputerList.Items.Count > 0)
                StatusText.Text = $"Đã cập nhật lúc {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 35, 24));
            StatusText.Text = $"Không cập nhật được: {ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (TargetClient is null || PrinterName is null) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
