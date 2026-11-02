namespace RemotePrint.Client;

public sealed class InstalledPrintersForm : Form
{
    private readonly ServiceClient _service;
    internal readonly ListBox Printers = new() { Dock = DockStyle.Fill, DisplayMember = nameof(InstalledPrinter.Name), HorizontalScrollbar = true };
    internal readonly Button RemoveButton = MainForm.NewButton(L.T("Xóa máy in"));
    private readonly Button _refresh = MainForm.NewButton(L.T("Làm mới"));
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private bool _busy;

    public InstalledPrintersForm(ServiceClient service)
    {
        _service = service;
        Text = L.T("Máy in đã cài");
        Icon = Program.AppIcon;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(560, 300);
        MinimumSize = new Size(480, 260);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.Controls.Add(Printers, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = MainForm.NewButton(L.T("Đóng"));
        close.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([close, RemoveButton, _refresh]);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        CancelButton = close;
        Printers.SelectedIndexChanged += (_, _) => UpdateButtons();
        _refresh.Click += async (_, _) => await RefreshPrintersAsync();
        RemoveButton.Click += async (_, _) =>
        {
            if (Printers.SelectedItem is not InstalledPrinter printer) return;
            if (MessageBox.Show(this, string.Format(L.T("Xóa máy in {0} trên máy này? Driver dùng chung và file đã nhận được giữ lại."), printer.Name),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _busy = true; UpdateButtons();
            try
            {
                var snapshot = await _service.CallAsync(new("remove-printer", QueueId: printer.Id));
                if (!IsDisposed) ApplySnapshot(snapshot);
            }
            catch (Exception ex) { if (!IsDisposed) _status.Text = L.Message(ex.Message); }
            finally { if (!IsDisposed) { _busy = false; UpdateButtons(); } }
        };
        Shown += async (_, _) => await RefreshPrintersAsync();
        UpdateButtons();
    }

    internal void ApplySnapshot(ServiceSnapshot snapshot)
    {
        var selected = (Printers.SelectedItem as InstalledPrinter)?.Id;
        Printers.BeginUpdate();
        Printers.Items.Clear();
        foreach (var printer in snapshot.InstalledPrinters) Printers.Items.Add(printer);
        for (var i = 0; i < Printers.Items.Count; i++)
            if (((InstalledPrinter)Printers.Items[i]).Id == selected) Printers.SelectedIndex = i;
        Printers.EndUpdate();
        _status.Text = Printers.Items.Count == 0 ? L.T("Chưa có máy in nào được cài qua Remote Print.")
            : L.T("Chọn máy in đã cài trên máy này để xóa.");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        RemoveButton.Enabled = !_busy && Printers.SelectedItem is InstalledPrinter;
        _refresh.Enabled = !_busy;
        Printers.Enabled = !_busy;
    }

    private async Task RefreshPrintersAsync()
    {
        if (_busy) return;
        _busy = true; UpdateButtons();
        try
        {
            var snapshot = await _service.CallAsync(new("status"));
            if (!IsDisposed) ApplySnapshot(snapshot);
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = L.Message(ex.Message); }
        finally { if (!IsDisposed) { _busy = false; UpdateButtons(); } }
    }
}
