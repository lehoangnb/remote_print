using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;

namespace RemotePrint.Client;

// Console window of the desktop agent. It only talks to the Windows Service over the
// named pipe, so closing the window hides it and keeps the service running.
public sealed class MainForm : Form
{
    private const int Edge = 12;
    private const int ColumnGap = 10;

    // A themed button loses ~6 px to its own border, and stacked Vietnamese diacritics
    // need that room back, so every action row reserves 31 px to land at 25 px tall.
    private const int ButtonRow = 31;
    private const int ButtonHeight = 25;

    private readonly ServiceClient _service;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly bool _headless;
    private readonly MenuStrip _menu = new();
    private readonly ToolStripMenuItem _serviceInstallItem = new(L.T("Cài / khởi động dịch vụ"));
    private readonly ToolStripMenuItem _serviceUninstallItem = new(L.T("Gỡ cài đặt dịch vụ"));
    private readonly ToolStripMenuItem _testPrinterInstallItem = new(L.T("Cài máy in ảo để thử…"));
    private readonly ToolStripMenuItem _testPrinterUninstallItem = new(L.T("Gỡ máy in test…"));
    internal readonly ToolStripMenuItem InstalledPrintersItem = new(L.T("Máy in đã cài…")) { Enabled = false };
    private readonly ToolStripMenuItem _clearTestFilesItem = new(L.T("Dọn file in thử…"));
    private ServiceSnapshot? _snapshot;
    private bool _refreshing, _settingsLoaded, _servicePrompted, _configPrompted, _setupPromptOpen;
    private bool _connectingRemotePrinter;
    private bool _clearingTestFiles;
    private readonly HashSet<string> _printerDraft = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _availablePrinterNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _printerDraftLoaded, _printerDraftDirty, _updatingPrinterList;
    private bool _savingSettings;
    private int _printerDraftVersion;
    private string _deviceName = "", _serverUrl = "";

    public Label ServiceStatusText { get; } = NewValue(L.T("Đang kiểm tra…"));
    public Label StatusText { get; } = NewValue(L.T("Chưa kết nối"));
    public Label RoomText { get; } = NewValue(L.T("Chưa được gán"));
    public Label TunnelText { get; } = NewValue(L.T("Chưa cấu hình"));
    public Label PrintersHintText { get; } = new()
    {
        Text = "", AutoSize = false, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText,
        TextAlign = ContentAlignment.MiddleLeft
    };
    public Label PeersSummaryText { get; } = NewHint(L.T("Chỉ các máy online mới xuất hiện."), 20);
    public Label EmptyPeersPanel { get; } = new()
    {
        Text = L.T("Chưa có máy khác online\r\nKết nối client trên máy khác và duyệt vào cùng Room để in từ xa."),
        AutoSize = false, Dock = DockStyle.Fill, Visible = false, TextAlign = ContentAlignment.MiddleCenter,
        BackColor = SystemColors.Window
    };
    public CheckedListBox PrintersList { get; } = new()
    {
        CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.Window, HorizontalScrollbar = true
    };
    public PeerListBox PeersGrid { get; } = new();
    public ComboBox RemotePrinters { get; } = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 0, 6, 0)
    };
    public Button ConnectPrinterButton { get; } = NewButton(L.T("Kết nối máy in"));
    public Button PrintTestButton { get; } = new()
    {
        Text = L.T("In thử"), Width = 72, Height = ButtonHeight, Enabled = false, Margin = new Padding(0)
    };
    public WrappedListBox ActivityList { get; } = new() { Font = new Font("Segoe UI", 8.25F) };
    public Button OpenTestFilesButton { get; } = NewButton(L.T("Mở file in thử…"));
    public Button ScanPrintersButton { get; } = new()
    {
        Text = L.T("Quét lại"), AutoSize = false, Width = 88, Height = ButtonHeight,
        Anchor = AnchorStyles.Right, Margin = new Padding(0)
    };

    public MainForm(string? pipeName = null, bool headless = false)
    {
        Icon = Program.AppIcon;
        _service = new ServiceClient(pipeName);
        _headless = headless;
        BuildMenu();
        BuildLayout();
        PrintersList.ItemCheck += (_, e) =>
        {
            if (_updatingPrinterList || !_printerDraftLoaded || PrintersList.Items[e.Index] is not string name) return;
            if (e.NewValue == CheckState.Checked) _printerDraft.Add(name); else _printerDraft.Remove(name);
            _printerDraftDirty = true;
            _printerDraftVersion++;
        };
        PrintersList.FormattingEnabled = true;
        PrintersList.Format += (_, e) =>
        {
            if (e.ListItem is string name && !_availablePrinterNames.Contains(name)) e.Value = name + L.T(" (không khả dụng)");
        };
        _timer.Tick += async (_, _) => { await RefreshAsync(); await SavePrinterSelectionAsync(); };
        _timer.Start();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
        if (headless)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-2400, -2400);
            ShowInTaskbar = false;
        }
        _ = RefreshAsync();
    }

    internal event EventHandler? LanguageChanged;

    internal void ApplyLanguage(string language)
    {
        var previous = L.Language;
        if (previous == language) return;
        L.UseLanguage(language);
        void TranslateMenu(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.Text = L.Retranslate(item.Text ?? "", previous, language);
                if (item is ToolStripMenuItem menuItem) TranslateMenu(menuItem.DropDownItems);
            }
        }
        void TranslateControls(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is Label or Button or GroupBox)
                    control.Text = L.Retranslate(control.Text, previous, language);
                var tip = control.GetTip();
                if (tip is not null) control.SetTip(L.Retranslate(tip, previous, language));
                if (control is ToolStrip strip) TranslateMenu(strip.Items);
                TranslateControls(control);
            }
        }
        SuspendLayout();
        TranslateControls(this);
        if (_snapshot is not null) ApplySnapshot(_snapshot);
        PeersGrid.Invalidate();
        ResumeLayout(true);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StopAgent() => _timer.Stop();

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopAgent();
        base.OnFormClosed(e);
    }

    protected override bool ShowWithoutActivation => _headless;

    // One-off machine setup lives in a menu instead of a wall of buttons: the
    // actions are needed once per install, not on every visit to the window.
    private void BuildMenu()
    {
        var configure = new ToolStripMenuItem(L.T("Cấu hình máy và server…"));
        configure.Click += (_, _) => OpenConfiguration();
        var settings = new ToolStripMenuItem(L.T("Cài đặt"));
        settings.DropDownItems.AddRange([
            configure, new ToolStripSeparator(),
            _serviceInstallItem, _serviceUninstallItem, new ToolStripSeparator(), InstalledPrintersItem,
            _testPrinterInstallItem, _testPrinterUninstallItem, _clearTestFilesItem
        ]);
        _serviceInstallItem.Click += InstallService_Click;
        _serviceUninstallItem.Click += UninstallService_Click;
        _testPrinterInstallItem.Click += async (_, _) => await InstallTestPrinterAsync();
        InstalledPrintersItem.Click += (_, _) =>
        {
            using var dialog = new InstalledPrintersForm(_service);
            dialog.ShowDialog(this);
            _ = RefreshAsync();
        };
        _testPrinterUninstallItem.Click += async (_, _) => await UninstallTestPrinterAsync();
        _clearTestFilesItem.Click += async (_, _) =>
        {
            if (_clearingTestFiles) return;
            if (MessageBox.Show(this, L.T("Xóa tất cả file in thử đã nhận và bản sao đã mở trên máy này?"), L.T("Dọn file in thử"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _clearTestFilesItem.Enabled = false;
            _clearingTestFiles = true;
            try { await TestPrintFilesForm.ClearFilesAsync(_service); }
            catch (Exception ex) { ShowError(ex); }
            finally { _clearingTestFiles = false; _clearTestFilesItem.Enabled = _snapshot?.TestFileMaintenance == true; }
        };
        _menu.Items.Add(settings);
        var language = new ToolStripMenuItem("Language / Ngôn ngữ");
        foreach (var (code, label) in new[] { ("vi", "Tiếng Việt"), ("en", "English") })
        {
            var item = new ToolStripMenuItem(label) { Checked = L.Language == code };
            item.Click += (_, _) =>
            {
                try { L.SetLanguage(code); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { MessageBox.Show(L.Message(ex.Message), "Remote Print", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                ApplyLanguage(code);
                foreach (ToolStripMenuItem choice in language.DropDownItems) choice.Checked = choice == item;
            };
            language.DropDownItems.Add(item);
        }
        _menu.Items.Add(language);
    }

    private void BuildLayout()
    {
        Text = "Remote Print";
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(700, 470);
        MainMenuStrip = _menu;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1,
            Padding = new Padding(Edge, 8, Edge, Edge)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Name = "Body" };
        body.ColumnStyles.AddRange([
            new ColumnStyle(SizeType.Absolute, 292F), new ColumnStyle(SizeType.Absolute, 12F),
            new ColumnStyle(SizeType.Percent, 100F)
        ]);
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        body.Controls.Add(BuildLeftColumn(), 0, 0);
        body.Controls.Add(BuildRightColumn(), 2, 0);
        root.Controls.Add(body, 0, 0);

        // The docking layout walks the children from the last added backwards, so the
        // menu has to come after the Fill panel to keep its strip at the window edge.
        Controls.Add(root);
        Controls.Add(_menu);
    }

    private Control BuildLeftColumn()
    {
        var column = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        column.RowStyles.AddRange([
            new RowStyle(SizeType.Absolute, 150), new RowStyle(SizeType.Percent, 100F)
        ]);

        var statusGroup = NewGroup(L.T("Trạng thái máy này"));
        var statusGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 4
        };
        statusGrid.ColumnStyles.AddRange([
            new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100F)
        ]);
        statusGrid.RowStyles.AddRange([
            new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize),
            new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize)
        ]);
        AddRow(statusGrid, 0, "Windows Service:", ServiceStatusText);
        AddRow(statusGrid, 1, L.T("Kết nối server:"), StatusText);
        AddRow(statusGrid, 2, "Room:", RoomText);
        AddRow(statusGrid, 3, "Tunnel:", TunnelText);
        statusGroup.Controls.Add(statusGrid);
        column.Controls.Add(statusGroup, 0, 0);

        var sharedGroup = NewGroup(L.T("Máy in chia sẻ"));
        var sharedGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        sharedGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        sharedGrid.RowStyles.AddRange([
            new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.Percent, 100F),
            new RowStyle(SizeType.Absolute, ButtonRow)
        ]);
        sharedGrid.Controls.Add(NewHint(L.T("Chọn máy in để máy cùng Room sử dụng."), 18), 0, 0);
        PrintersList.Dock = DockStyle.Fill;
        sharedGrid.Controls.Add(PrintersList, 0, 1);
        var sharedBottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        sharedBottom.ColumnStyles.AddRange([
            new ColumnStyle(SizeType.Percent, 100F), new ColumnStyle(SizeType.AutoSize)
        ]);
        sharedBottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        sharedBottom.Controls.Add(PrintersHintText, 0, 0);
        ScanPrintersButton.Click += ScanPrintersButton_Click;
        sharedBottom.Controls.Add(ScanPrintersButton, 1, 0);
        sharedGrid.Controls.Add(sharedBottom, 0, 2);
        sharedGroup.Controls.Add(sharedGrid);
        column.Controls.Add(sharedGroup, 0, 1);
        return column;
    }

    private Control BuildRightColumn()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6,
            SplitterDistance = 250, Panel1MinSize = 130, Panel2MinSize = 100, IsSplitterFixed = false
        };
        var peersGroup = NewGroup(L.T("Máy cùng Room"));
        peersGroup.Dock = DockStyle.Fill;
        var peersGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        peersGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        peersGrid.RowStyles.AddRange([
            new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.Percent, 100F), new RowStyle(SizeType.Absolute, ButtonRow)
        ]);
        peersGrid.Controls.Add(PeersSummaryText, 0, 0);
        var peersHost = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
        PeersGrid.Dock = DockStyle.Fill;
        PeersGrid.BorderStyle = BorderStyle.None;
        peersHost.Controls.Add(PeersGrid);
        // The placeholder and the list both fill the host, so only one of them can be
        // visible at a time; a docked overlay would be left with no room.
        EmptyPeersPanel.Dock = DockStyle.Fill;
        peersHost.Controls.Add(EmptyPeersPanel);
        peersGrid.Controls.Add(peersHost, 0, 1);
        var connectRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        connectRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        connectRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        connectRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        connectRow.Controls.Add(RemotePrinters, 0, 0);
        connectRow.Controls.Add(ConnectPrinterButton, 1, 0);
        connectRow.Controls.Add(PrintTestButton, 2, 0);
        ConnectPrinterButton.Anchor = AnchorStyles.Left;
        PrintTestButton.Anchor = AnchorStyles.Left;
        ConnectPrinterButton.Enabled = false;
        PeersGrid.SelectedIndexChanged += (_, _) => UpdateRemotePrinters();
        RemotePrinters.SelectedIndexChanged += (_, _) => UpdateConnectButton();
        ConnectPrinterButton.Click += async (_, _) => await ConnectRemotePrinterAsync();
        PrintTestButton.Click += async (_, _) => await PrintRemoteTestAsync();
        peersGrid.Controls.Add(connectRow, 0, 2);
        peersGroup.Controls.Add(peersGrid);
        split.Panel1.Controls.Add(peersGroup);

        var activityGroup = NewGroup(L.T("Hoạt động gần đây"));
        activityGroup.Dock = DockStyle.Fill;
        ActivityList.Dock = DockStyle.Fill;
        ActivityList.BorderStyle = BorderStyle.FixedSingle;
        var activityLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ButtonRow));
        activityLayout.Controls.Add(ActivityList, 0, 0);
        OpenTestFilesButton.Width = 150;
        OpenTestFilesButton.Anchor = AnchorStyles.Right;
        OpenTestFilesButton.Enabled = false;
        OpenTestFilesButton.Visible = false;
        OpenTestFilesButton.Click += (_, _) =>
        {
            using var dialog = new TestPrintFilesForm(_service);
            dialog.ShowDialog(this);
        };
        activityLayout.Controls.Add(OpenTestFilesButton, 0, 1);
        activityGroup.Controls.Add(activityLayout);
        split.Panel2.Controls.Add(activityGroup);
        return split;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string caption, Label value)
    {
        grid.Controls.Add(NewCaption(caption), 0, row);
        grid.Controls.Add(value, 1, row);
    }

    private static GroupBox NewGroup(string header) => new()
    {
        Text = header, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, ColumnGap),
        Padding = new Padding(6, 4, 6, 6)
    };

    private static Label NewCaption(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 2)
    };

    private static Label NewValue(string text) => new()
    {
        Text = text, AutoSize = false, Height = 20, Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 9F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 4, 0, 2)
    };

    private static Label NewHint(string text, int height) => new()
    {
        Text = text, AutoSize = false, Height = height, Dock = DockStyle.Fill,
        ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6)
    };

    internal static Button NewButton(string text) => new()
    {
        Text = text, AutoSize = false, Width = 128, Height = ButtonHeight, Margin = new Padding(0, 0, 6, 0)
    };

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            ApplySnapshot(await _service.CallAsync(new("status")));
        }
        catch (Exception ex)
        {
            ServiceStatusText.Text = L.T("Chưa chạy");
            StatusText.Text = L.T("Chưa kết nối dịch vụ");
            TunnelText.Text = L.T("Chưa kết nối dịch vụ");
            _serviceInstallItem.Text = L.T("Cài / khởi động dịch vụ");
            _serviceUninstallItem.Enabled = false;
            // The reason stays out of the layout but one hover away.
            ServiceStatusText.SetTip(L.Message(ex.Message));
            _snapshot = null;
            InstalledPrintersItem.Enabled = false;
            OpenTestFilesButton.Enabled = false;
            OpenTestFilesButton.Visible = false;
            _testPrinterUninstallItem.Enabled = false;
            _clearTestFilesItem.Enabled = false;
            _testPrinterInstallItem.Enabled = false;
            UpdateConnectButton();
            PeersGrid.Items.Clear();
            ShowEmptyPeers(true);
            if (!_settingsLoaded)
            {
                var old = new ConfigService().LoadOrCreate();
                _deviceName = old.ComputerName;
                _serverUrl = old.ServerUrl;
                _settingsLoaded = true;
            }
            PromptSetup(serviceMissing: true);
        }
        finally { _refreshing = false; }
    }

    private void ApplySnapshot(ServiceSnapshot snapshot)
    {
        _snapshot = snapshot;
        InstalledPrintersItem.Enabled = snapshot.PrinterRemoval;
        OpenTestFilesButton.Visible = snapshot.TestPrinterInstalled;
        OpenTestFilesButton.Enabled = snapshot.TestPrinterInstalled && snapshot.TestFileAccess;
        _testPrinterUninstallItem.Enabled = snapshot.TestPrinterInstalled && snapshot.TestFileMaintenance && !_installingTestPrinter;
        _clearTestFilesItem.Enabled = snapshot.TestFileMaintenance && !_clearingTestFiles;
        _testPrinterInstallItem.Enabled = snapshot.TestPrinting && !_installingTestPrinter;
        ServiceStatusText.Text = L.T("Đang chạy");
        ServiceStatusText.SetTip(snapshot.NativePrinting ? null : L.T("Cần cập nhật dịch vụ để dùng relay driver/RAW."));
        StatusText.Text = L.T(snapshot.Connection);
        RoomText.Text = snapshot.State.Room ?? L.T("Chưa được gán");
        TunnelText.Text = L.Message(snapshot.TunnelStatus);
        TunnelText.SetTip(L.Message(snapshot.TunnelStatus));
        _serviceInstallItem.Text = L.T("Cập nhật dịch vụ");
        _serviceUninstallItem.Enabled = true;
        if (!_settingsLoaded)
        {
            _deviceName = snapshot.Settings.ComputerName;
            _serverUrl = snapshot.Settings.ServerUrl;
            _settingsLoaded = true;
        }
        if (!_printerDraftLoaded || !_printerDraftDirty)
        {
            _printerDraft.Clear();
            _printerDraft.UnionWith(snapshot.Settings.SharedPrinters);
            _printerDraftLoaded = true;
        }
        _availablePrinterNames = snapshot.Printers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visiblePrinters = snapshot.Printers.Concat(_printerDraft.Where(name => !_availablePrinterNames.Contains(name))
            .Order(StringComparer.CurrentCultureIgnoreCase)).ToList();
        if (!PrintersList.Items.Cast<string>().SequenceEqual(visiblePrinters))
        {
            _updatingPrinterList = true;
            PrintersList.BeginUpdate();
            try
            {
                PrintersList.Items.Clear();
                foreach (var name in visiblePrinters)
                {
                    var index = PrintersList.Items.Add(name);
                    if (_printerDraft.Contains(name)) PrintersList.SetItemChecked(index, true);
                }
            }
            finally { PrintersList.EndUpdate(); _updatingPrinterList = false; }
        }
        else
        {
            _updatingPrinterList = true;
            try
            {
                for (var index = 0; index < PrintersList.Items.Count; index++)
                {
                    var shouldCheck = _printerDraft.Contains((string)PrintersList.Items[index]);
                    if (PrintersList.GetItemChecked(index) != shouldCheck) PrintersList.SetItemChecked(index, shouldCheck);
                }
            }
            finally { _updatingPrinterList = false; }
        }
        PrintersList.Refresh();
        var pendingShareChange = !_printerDraft.SetEquals(snapshot.Settings.SharedPrinters);
        PrintersHintText.Text = snapshot.Printers.Count == 0
            ? L.T("Dịch vụ chưa thấy máy in. Cài máy in cho toàn bộ máy tính rồi quét lại.")
            : pendingShareChange
                ? L.T("Đang lưu lựa chọn máy in…")
                : string.Format(L.T("{0} máy in khả dụng cho dịch vụ."), snapshot.Printers.Count);
        if (!SamePeerRows(PeersGrid.Items.Cast<PeerClient>(), snapshot.Peers))
        {
            var selectedId = (PeersGrid.SelectedItem as PeerClient)?.Id;
            var selectedPrinter = RemotePrinters.SelectedItem as string;
            PeersGrid.BeginUpdate();
            PeersGrid.Items.Clear();
            foreach (var peer in snapshot.Peers) PeersGrid.Items.Add(peer);
            if (selectedId is not null)
                for (var index = 0; index < PeersGrid.Items.Count; index++)
                    if (((PeerClient)PeersGrid.Items[index]).Id == selectedId) { PeersGrid.SelectedIndex = index; break; }
            PeersGrid.EndUpdate();
            if (selectedPrinter is not null && RemotePrinters.Items.Contains(selectedPrinter)) RemotePrinters.SelectedItem = selectedPrinter;
        }
        ShowEmptyPeers(snapshot.Peers.Count == 0);
        UpdateConnectButton();
        PeersSummaryText.Text = string.Format(L.T("{0} máy online · chỉ hiển thị trong cùng Room"), snapshot.Peers.Count);
        var activity = snapshot.Activity.Select(item => $"{item.At:HH:mm:ss}  {L.Message(item.Message)}").ToList();
        if (!ActivityList.Items.Cast<string>().SequenceEqual(activity))
        {
            ActivityList.BeginUpdate();
            ActivityList.Items.Clear();
            foreach (var line in activity) ActivityList.Items.Add(line);
            ActivityList.EndUpdate();
        }
        PromptSetup(serviceMissing: false);
    }

    private static bool SamePeerRows(IEnumerable<PeerClient> previous, IEnumerable<PeerClient> current)
    {
        return previous.Zip(current).All(pair => pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.TunnelAddress == pair.Second.TunnelAddress && pair.First.TunnelPath == pair.Second.TunnelPath && pair.First.Printers.SequenceEqual(pair.Second.Printers) &&
            pair.First.NativePrinting == pair.Second.NativePrinting &&
            pair.First.WindowsPrinterShares.Count == pair.Second.WindowsPrinterShares.Count &&
            pair.First.WindowsPrinterShares.All(entry => pair.Second.WindowsPrinterShares.TryGetValue(entry.Key, out var value) && value == entry.Value))
            && previous.Count() == current.Count();
    }

    private void UpdateRemotePrinters()
    {
        RemotePrinters.Items.Clear();
        if (PeersGrid.SelectedItem is PeerClient peer)
            foreach (var printer in peer.NativePrinting ? peer.Printers : []) RemotePrinters.Items.Add(printer);
        if (RemotePrinters.Items.Count > 0) RemotePrinters.SelectedIndex = 0;
        UpdateConnectButton();
    }

    private void UpdateConnectButton()
    {
        ConnectPrinterButton.Enabled = !_connectingRemotePrinter && _snapshot?.NativePrinting == true && _snapshot.TunnelStatus == "Đã kết nối"
            && PeersGrid.SelectedItem is PeerClient { TunnelAddress: not null } && RemotePrinters.SelectedItem is string;
        PrintTestButton.Enabled = ConnectPrinterButton.Enabled && _snapshot?.TestPrinting == true && _snapshot.TestPdfPrinting
            && string.Equals(RemotePrinters.SelectedItem as string, TestPrinter.Name, StringComparison.OrdinalIgnoreCase);
        PrintTestButton.SetTip(L.T("Kết nối Remote Print Test trước, rồi gửi trang thử và xem nhật ký trên máy nhận."));
    }

    private bool _installingTestPrinter;
    private async Task UninstallTestPrinterAsync()
    {
        if (MessageBox.Show(this, L.T("Gỡ máy in Remote Print Test trên máy này? File đã nhận được giữ lại."),
                L.T("Gỡ máy in test"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _installingTestPrinter = true;
        _testPrinterUninstallItem.Enabled = false;
        _testPrinterInstallItem.Enabled = false;
        try
        {
            var snapshot = await _service.CallAsync(new("uninstall-test-printer"));
            if (_printerDraft.Remove(TestPrinter.Name)) _printerDraftVersion++;
            ApplySnapshot(snapshot);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _installingTestPrinter = false;
            _testPrinterInstallItem.Enabled = _snapshot?.TestPrinting == true;
            _testPrinterUninstallItem.Enabled = _snapshot is { TestPrinterInstalled: true, TestFileMaintenance: true };
        }
    }
    private async Task InstallTestPrinterAsync()
    {
        _installingTestPrinter = true;
        _testPrinterInstallItem.Enabled = false;
        try
        {
            ApplySnapshot(await _service.CallAsync(new("install-test-printer")));
            MessageBox.Show(this, L.T("Đã cài Remote Print Test. Tích chọn máy in này trong Máy in chia sẻ. Trên client khác cùng Room, kết nối máy in rồi bấm In thử. Xem Hoạt động gần đây ở máy nhận để xác nhận."),
                "Remote Print", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _installingTestPrinter = false; _testPrinterInstallItem.Enabled = _snapshot?.TestPrinting == true; }
    }

    private async Task PrintRemoteTestAsync()
    {
        if (!PrintTestButton.Enabled || PeersGrid.SelectedItem is not PeerClient peer || RemotePrinters.SelectedItem is not string printer) return;
        _connectingRemotePrinter = true;
        UpdateConnectButton();
        try
        {
            ApplySnapshot(await _service.CallAsync(new("print-test", PeerId: peer.Id, Printer: printer)));
            MessageBox.Show(this, L.T("Đã gửi trang thử vào hàng đợi Windows. Xem Hoạt động gần đây trên máy nhận để xác nhận máy in ảo đã nhận dữ liệu."),
                "Remote Print", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _connectingRemotePrinter = false; UpdateConnectButton(); }
    }

    private async Task ConnectRemotePrinterAsync()
    {
        if (PeersGrid.SelectedItem is not PeerClient peer || peer.TunnelAddress is null ||
            RemotePrinters.SelectedItem is not string printer || !peer.NativePrinting) return;
        _connectingRemotePrinter = true;
        UpdateConnectButton();
        try
        {
            ApplySnapshot(await _service.CallAsync(new("connect-printer", PeerId: peer.Id, Printer: printer)));
            MessageBox.Show(this, string.Format(L.T("Windows đã kết nối máy in {0}."), printer), "Remote Print", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, L.Message(ex.Message), L.T("Không kết nối được máy in"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _connectingRemotePrinter = false; UpdateConnectButton(); }
    }

    private static bool IsConfigured(ServiceSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.Settings.ServerUrl);

    // A fresh install lands on a window that cannot do anything yet, so the first
    // time each gap is seen the window explains it once and lets the user skip.
    private void PromptSetup(bool serviceMissing)
    {
        if (_headless || _setupPromptOpen || !Visible || !IsHandleCreated) return;
        if (serviceMissing ? _servicePrompted : _configPrompted) return;
        if (!serviceMissing && _snapshot is null) return;
        if (!serviceMissing && IsConfigured(_snapshot!)) return;
        if (serviceMissing) _servicePrompted = true; else _configPrompted = true;
        _setupPromptOpen = true;
        try
        {
            if (serviceMissing)
            {
                using var notice = new SetupNoticeForm();
                if (notice.ShowDialog(this) == DialogResult.OK) _ = InstallService();
            }
            else
            {
                OpenConfiguration();
            }
        }
        finally { _setupPromptOpen = false; }
    }

    private void OpenConfiguration()
    {
        using var dialog = new DeviceConfigForm(_deviceName, _serverUrl);
        if (dialog.ShowDialog(Visible ? this : null) != DialogResult.OK) return;
        _deviceName = dialog.ComputerName;
        _serverUrl = dialog.ServerUrl;
        _ = SaveAndConnectAsync();
    }

    private void ShowEmptyPeers(bool empty)
    {
        EmptyPeersPanel.Visible = empty;
        PeersGrid.Visible = !empty;
    }

    private List<string> SharedPrinterSelection() => _printerDraft.ToList();

    private async Task SavePrinterSelectionAsync()
    {
        if (!_printerDraftDirty || _savingSettings || _snapshot is null || !IsConfigured(_snapshot)) return;
        _savingSettings = true;
        var version = _printerDraftVersion;
        try
        {
            var settings = _snapshot.Settings;
            var updated = await _service.CallAsync(new("configure",
                new DeviceSettings(settings.ComputerName, settings.ServerUrl, SharedPrinterSelection())));
            if (version == _printerDraftVersion) _printerDraftDirty = false;
            ApplySnapshot(updated);
            _serverUrl = updated.Settings.ServerUrl;
        }
        catch (Exception ex)
        {
            PrintersHintText.Text = L.T("Chưa lưu được, sẽ thử lại.");
            PrintersHintText.SetTip(L.Message(ex.Message));
        }
        finally { _savingSettings = false; }
    }

    private async Task SaveAndConnectAsync()
    {
        if (_savingSettings) { ShowError(new InvalidOperationException(L.T("Đang lưu máy in, hãy thử lại."))); return; }
        _savingSettings = true;
        var version = _printerDraftVersion;
        try
        {
            var updated = await _service.CallAsync(new("configure",
                new DeviceSettings(_deviceName, _serverUrl, SharedPrinterSelection())));
            _serverUrl = updated.Settings.ServerUrl;
            if (version == _printerDraftVersion) _printerDraftDirty = false;
            ApplySnapshot(updated);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _savingSettings = false; }
    }

    private async void ScanPrintersButton_Click(object? sender, EventArgs e)
    {
        try { await _service.CallAsync(new("refresh")); await Task.Delay(2200); await RefreshAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task InstallService()
    {
        SetServiceItems(enabled: false);
        string? migration = null;
        try
        {
            migration = new ConfigService().PrepareServiceMigration();
            await ElevatedSetup.RunAsync(SetupOperation.InstallService, migration);
            _settingsLoaded = false;
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (migration is not null) File.Delete(migration);
            SetServiceItems(enabled: true);
        }
    }

    private async void InstallService_Click(object? sender, EventArgs e) => await InstallService();

    private async void UninstallService_Click(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this,
                L.T("Gỡ Windows Service Remote Print và mục khởi động cùng Windows của máy này?\n\n") +
                L.T("Cấu hình được giữ lại. Máy khác không in tới máy này ") +
                L.T("cho tới khi cài lại dịch vụ."),
                L.T("Gỡ cài đặt dịch vụ"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        SetServiceItems(enabled: false);
        var removed = false;
        try
        {
            await ElevatedSetup.RunAsync(SetupOperation.UninstallService, migrationFile: null);
            _settingsLoaded = false;
            removed = true;
            MessageBox.Show(this,
                L.T("Đã gỡ Windows Service. Dữ liệu cấu hình vẫn nằm trong ProgramData\\RemotePrint."),
                L.T("Gỡ cài đặt dịch vụ"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            SetServiceItems(enabled: true);
            // A second click would only report a removal that already happened;
            // ApplySnapshot re-enables the item after a reinstall.
            _serviceUninstallItem.Enabled = !removed;
        }
        await RefreshAsync();
    }

    private void SetServiceItems(bool enabled)
    {
        _serviceInstallItem.Enabled = enabled;
        _serviceUninstallItem.Enabled = enabled;
    }

    private void ShowError(Exception ex) => MessageBox.Show(this, L.Message(ex.Message), "Remote Print",
        MessageBoxButtons.OK, MessageBoxIcon.Warning);
}

// Activity lines are free-form and often longer than the panel, so each row
// measures its own wrapped height instead of relying on a horizontal scrollbar.
public class WrappedListBox : ListBox
{
    public WrappedListBox()
    {
        DrawMode = DrawMode.OwnerDrawVariable;
        IntegralHeight = false;
        BorderStyle = BorderStyle.FixedSingle;
    }

    private string TextAt(int index) => index >= 0 && index < Items.Count ? Items[index]?.ToString() ?? "" : "";

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        base.OnMeasureItem(e);
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var available = Math.Max(40, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
        var height = TextRenderer.MeasureText(TextAt(e.Index), Font, new Size(available, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        e.ItemHeight = Math.Max(20, height + 4);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var color = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : SystemColors.WindowText;
        TextRenderer.DrawText(e.Graphics, TextAt(e.Index), e.Font ?? Font,
            new Rectangle(e.Bounds.X + 3, e.Bounds.Y + 2, Math.Max(1, e.Bounds.Width - 6), e.Bounds.Height),
            color, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Top);
        e.DrawFocusRectangle();
    }
}

// One peer per row: machine name plus the printers it shares.
public sealed class PeerListBox : ListBox
{
    private Font? _boldFont;

    public PeerListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 42;
        IntegralHeight = false;
        BorderStyle = BorderStyle.FixedSingle;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _boldFont?.Dispose();
        base.Dispose(disposing);
    }

    internal static string PeerTitle(PeerClient peer) => string.IsNullOrEmpty(peer.TunnelPath)
        ? peer.Name : $"{peer.Name} · {L.Message(peer.TunnelPath)}";

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not PeerClient peer) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var nameColor = selected ? SystemColors.HighlightText : SystemColors.WindowText;
        var summaryColor = selected ? SystemColors.HighlightText : SystemColors.GrayText;
        _boldFont ??= new Font(Font, FontStyle.Bold);
        var bounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 2, Math.Max(1, e.Bounds.Width - 8), e.Bounds.Height);
        var title = PeerTitle(peer);
        TextRenderer.DrawText(e.Graphics, title, _boldFont,
            new Rectangle(bounds.X, bounds.Y, bounds.Width, 18), nameColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, peer.PrinterSummary, Font,
            new Rectangle(bounds.X, bounds.Y + 19, bounds.Width, 20), summaryColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }
}
