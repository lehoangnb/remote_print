namespace RemotePrint.Client;

public sealed partial class InstalledPrintersForm : Form
{
    private readonly ServiceClient _service = new();
    private bool _busy;

    public InstalledPrintersForm()
    {
        InitializeComponent();
        if (UiHelpers.IsDesignMode(this)) return;
        Icon = Program.AppIcon;
        L.Apply(this);
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
        Shown += async (_, _) => { if (!UiHelpers.IsDesignMode(this)) await RefreshPrintersAsync(); };
        UpdateButtons();
    }

    public InstalledPrintersForm(ServiceClient service) : this()
    {
        _service = service;
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
