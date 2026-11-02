using System.Diagnostics;

namespace RemotePrint.Client;

public sealed class TestPrintFilesForm : Form
{
    private readonly ServiceClient _service;
    private readonly CancellationTokenSource _closing = new();
    private readonly ListView _files = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false
    };
    private readonly Button _open = MainForm.NewButton(L.T("Mở file"));
    private readonly Button _refresh = MainForm.NewButton(L.T("Làm mới"));
    private readonly Button _clear = MainForm.NewButton(L.T("Dọn tất cả"));
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private bool _busy;

    public TestPrintFilesForm(ServiceClient service)
    {
        _service = service;
        Text = L.T("File in thử đã nhận");
        Icon = Program.AppIcon;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(660, 340);
        MinimumSize = new Size(600, 280);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        _files.Columns.Add(L.T("Thời gian nhận"), 160);
        _files.Columns.Add(L.T("Kích thước (byte)"), 115, HorizontalAlignment.Right);
        _files.Columns.Add(L.T("Định dạng"), 85);
        _files.Columns.Add(L.T("Tên file"), 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.Controls.Add(_files, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = MainForm.NewButton(L.T("Đóng"));
        close.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([close, _open, _refresh, _clear]);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        CancelButton = close;
        AcceptButton = _open;
        _open.Enabled = false;
        _files.SelectedIndexChanged += (_, _) => UpdateButtons();
        _files.DoubleClick += async (_, _) => await OpenAsync();
        _open.Click += async (_, _) => await OpenAsync();
        _refresh.Click += async (_, _) => await RefreshFilesAsync();
        _clear.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, L.T("Xóa tất cả file in thử đã nhận và bản sao đã mở trên máy này?"), L.T("Dọn file in thử"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _busy = true; UpdateButtons();
            try { await ClearFilesAsync(_service); }
            catch (Exception ex) { if (!IsDisposed) { _status.Text = L.Message(ex.Message); _busy = false; UpdateButtons(); } return; }
            if (IsDisposed) return;
            _busy = false; await RefreshFilesAsync();
        };
        Shown += async (_, _) => await RefreshFilesAsync();
        FormClosed += (_, _) => _closing.Cancel();
    }

    private void UpdateButtons()
    {
        _open.Enabled = !_busy && _files.SelectedItems.Count == 1;
        _refresh.Enabled = !_busy;
        _clear.Enabled = !_busy;
    }

    internal static async Task ClearFilesAsync(ServiceClient service)
    {
        await service.CallAsync(new("clear-test-files"));
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePrint", "TestPrints");
        if (!Directory.Exists(folder)) return;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Thư mục file in thử không hợp lệ."));
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (Path.GetExtension(path) is not (".pdf" or ".txt" or ".part") || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            File.Delete(path);
        }
    }

    private async Task RefreshFilesAsync()
    {
        if (_busy) return;
        _busy = true; UpdateButtons();
        _status.Text = L.T("Đang tải…");
        try
        {
            var files = await _service.GetTestFilesAsync(_closing.Token);
            if (IsDisposed) return;
            var selectedId = _files.SelectedItems.Count == 1 ? (_files.SelectedItems[0].Tag as TestPrintFile)?.Id : null;
            _files.Items.Clear();
            foreach (var file in files)
            {
                var row = new ListViewItem([file.ReceivedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), file.Bytes.ToString("N0"),
                    file.Format == "pdf" ? "PDF" : file.HasContent ? L.T("Văn bản cũ") : L.T("Trống"), file.Id + (file.Format == "pdf" ? ".pdf" : ".txt")]) { Tag = file };
                _files.Items.Add(row);
                if (file.Id == selectedId) row.Selected = true;
            }
            if (_files.SelectedItems.Count == 0 && _files.Items.Count > 0) _files.Items[0].Selected = true;
            _status.Text = files.Count == 0 ? L.T("Chưa có file in thử trên máy này.") : L.T("Chọn file để mở PDF. Hiển thị tối đa 200 file mới nhất.");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) _status.Text = L.Message(ex.Message); }
        finally { if (!IsDisposed) { _busy = false; UpdateButtons(); } }
    }

    private async Task OpenAsync()
    {
        if (_busy || _files.SelectedItems.Count != 1 || _files.SelectedItems[0].Tag is not TestPrintFile file) return;
        _busy = true; UpdateButtons();
        _status.Text = L.T("Đang mở file in thử…");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePrint", "TestPrints");
        var scratch = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            if (!Guid.TryParseExact(file.Id, "N", out _)) throw new InvalidDataException(L.T("Mã file in thử không hợp lệ."));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, file.Id + (file.Format == "pdf" ? ".pdf" : ".txt"));
            await using (var output = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await _service.CopyTestFileAsync(file, output, _closing.Token);
            _closing.Token.ThrowIfCancellationRequested();
            File.Move(scratch, path, true);
            if (file.Format == "pdf")
            {
                using var viewer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else OpenTextFile(path);
            if (!IsDisposed) _status.Text = path;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) _status.Text = L.Message(ex.Message); }
        finally
        {
            try { if (File.Exists(scratch)) File.Delete(scratch); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!IsDisposed) { _busy = false; UpdateButtons(); }
        }
    }

    internal static void OpenTextFile(string path)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"))
        { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException(L.T("Không mở được Notepad."));
    }
}
