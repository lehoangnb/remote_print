// Layout and control properties are maintained by the Windows Forms Designer.
#nullable disable
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemotePrint.Client;

partial class MainForm
{
    private IContainer components;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAgent();
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new Container();
        _timer = new System.Windows.Forms.Timer(components);
        _menu = new MenuStrip();
        _settingsItem = new ToolStripMenuItem();
        _configureItem = new ToolStripMenuItem();
        _adminStartupItem = new ToolStripMenuItem();
        _checkUpdateItem = new ToolStripMenuItem();
        _serviceInstallItem = new ToolStripMenuItem();
        _serviceUninstallItem = new ToolStripMenuItem();
        _clientUninstallItem = new ToolStripMenuItem();
        _testPrinterInstallItem = new ToolStripMenuItem();
        _testPrinterUninstallItem = new ToolStripMenuItem();
        InstalledPrintersItem = new ToolStripMenuItem();
        _clearTestFilesItem = new ToolStripMenuItem();
        _languageItem = new ToolStripMenuItem();
        _vietnameseItem = new ToolStripMenuItem();
        _englishItem = new ToolStripMenuItem();
        _setupSeparator = new ToolStripSeparator();
        _printerSeparator = new ToolStripSeparator();
        rootLayout = new TableLayoutPanel();
        bodyLayout = new TableLayoutPanel();
        leftColumn = new TableLayoutPanel();
        statusGroup = new GroupBox();
        statusLayout = new TableLayoutPanel();
        statusCaption0 = new Label();
        ServiceStatusText = new Label();
        statusCaption1 = new Label();
        StatusText = new Label();
        statusCaption2 = new Label();
        RoomText = new Label();
        statusCaption3 = new Label();
        TunnelText = new Label();
        sharedGroup = new GroupBox();
        sharedLayout = new TableLayoutPanel();
        sharedHint = new Label();
        PrintersList = new CheckedListBox();
        sharedBottom = new TableLayoutPanel();
        PrintersHintText = new Label();
        ScanPrintersButton = new Button();
        rightSplit = new SplitContainer();
        peersGroup = new GroupBox();
        peersLayout = new TableLayoutPanel();
        PeersSummaryText = new Label();
        peersHost = new Panel();
        PeersGrid = new PeerListBox();
        EmptyPeersPanel = new Label();
        connectLayout = new TableLayoutPanel();
        RemotePrinters = new ComboBox();
        ConnectPrinterButton = new Button();
        PrintTestButton = new Button();
        activityGroup = new GroupBox();
        activityLayout = new TableLayoutPanel();
        ActivityList = new WrappedListBox();
        OpenTestFilesButton = new Button();
        _menu.SuspendLayout();
        rootLayout.SuspendLayout();
        bodyLayout.SuspendLayout();
        leftColumn.SuspendLayout();
        statusGroup.SuspendLayout();
        statusLayout.SuspendLayout();
        sharedGroup.SuspendLayout();
        sharedLayout.SuspendLayout();
        sharedBottom.SuspendLayout();
        ((ISupportInitialize)rightSplit).BeginInit();
        rightSplit.Panel1.SuspendLayout();
        rightSplit.Panel2.SuspendLayout();
        rightSplit.SuspendLayout();
        peersGroup.SuspendLayout();
        peersLayout.SuspendLayout();
        peersHost.SuspendLayout();
        connectLayout.SuspendLayout();
        activityGroup.SuspendLayout();
        activityLayout.SuspendLayout();
        SuspendLayout();
        //
        // _timer
        //
        _timer.Interval = 2000;
        //
        // _menu
        //
        _menu.Name = "_menu";
        _menu.Items.AddRange(new ToolStripItem[] { _settingsItem, _languageItem });
        //
        // _settingsItem
        //
        _settingsItem.Name = "_settingsItem";
        _settingsItem.Text = "Cài đặt";
        _settingsItem.DropDownItems.AddRange(new ToolStripItem[] { _configureItem, _adminStartupItem, _checkUpdateItem, _setupSeparator, _serviceInstallItem, _serviceUninstallItem, _clientUninstallItem, _printerSeparator, InstalledPrintersItem, _testPrinterInstallItem, _testPrinterUninstallItem, _clearTestFilesItem });
        //
        // _configureItem
        //
        _configureItem.Name = "_configureItem";
        _configureItem.Text = "Cấu hình máy và server…";
        //
        // _adminStartupItem
        //
        _adminStartupItem.Name = "_adminStartupItem";
        _adminStartupItem.Text = "Luôn chạy với quyền quản trị";
        _adminStartupItem.ToolTipText = "Áp dụng cả khi khởi động cùng Windows.";
        //
        // _checkUpdateItem
        //
        _checkUpdateItem.Name = "_checkUpdateItem";
        _checkUpdateItem.Text = "Cập nhật client…";
        //
        // _serviceInstallItem
        //
        _serviceInstallItem.Name = "_serviceInstallItem";
        _serviceInstallItem.Text = "Cài / khởi động dịch vụ";
        //
        // _serviceUninstallItem
        //
        _serviceUninstallItem.Name = "_serviceUninstallItem";
        _serviceUninstallItem.Text = "Gỡ cài đặt dịch vụ";
        //
        // _clientUninstallItem
        //
        _clientUninstallItem.Name = "_clientUninstallItem";
        _clientUninstallItem.Text = "Gỡ Remote Print khỏi máy…";
        //
        // _testPrinterInstallItem
        //
        _testPrinterInstallItem.Name = "_testPrinterInstallItem";
        _testPrinterInstallItem.Text = "Cài máy in ảo để thử…";
        //
        // _testPrinterUninstallItem
        //
        _testPrinterUninstallItem.Name = "_testPrinterUninstallItem";
        _testPrinterUninstallItem.Text = "Gỡ máy in test…";
        //
        // InstalledPrintersItem
        //
        InstalledPrintersItem.Name = "InstalledPrintersItem";
        InstalledPrintersItem.Text = "Máy in đã cài…";
        InstalledPrintersItem.Enabled = false;
        //
        // _clearTestFilesItem
        //
        _clearTestFilesItem.Name = "_clearTestFilesItem";
        _clearTestFilesItem.Text = "Dọn file in thử…";
        //
        // _languageItem
        //
        _languageItem.Name = "_languageItem";
        _languageItem.Text = "Language / Ngôn ngữ";
        _languageItem.DropDownItems.AddRange(new ToolStripItem[] { _vietnameseItem, _englishItem });
        //
        // _vietnameseItem
        //
        _vietnameseItem.Name = "_vietnameseItem";
        _vietnameseItem.Text = "Tiếng Việt";
        _vietnameseItem.Checked = true;
        //
        // _englishItem
        //
        _englishItem.Name = "_englishItem";
        _englishItem.Text = "English";
        //
        // _setupSeparator
        //
        _setupSeparator.Name = "_setupSeparator";
        //
        // _printerSeparator
        //
        _printerSeparator.Name = "_printerSeparator";
        //
        // rootLayout
        //
        rootLayout.Name = "rootLayout";
        rootLayout.Dock = DockStyle.Fill;
        rootLayout.ColumnCount = 1;
        rootLayout.RowCount = 1;
        rootLayout.Padding = new Padding(12, 8, 12, 12);
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        //
        // bodyLayout
        //
        bodyLayout.Name = "Body";
        bodyLayout.Dock = DockStyle.Fill;
        bodyLayout.ColumnCount = 3;
        bodyLayout.RowCount = 1;
        bodyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292F));
        bodyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12F));
        bodyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bodyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        //
        // leftColumn
        //
        leftColumn.Name = "leftColumn";
        leftColumn.Dock = DockStyle.Fill;
        leftColumn.ColumnCount = 1;
        leftColumn.RowCount = 2;
        leftColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 150F));
        leftColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        //
        // statusGroup
        //
        statusGroup.Name = "statusGroup";
        statusGroup.Text = "Trạng thái máy này";
        statusGroup.Dock = DockStyle.Fill;
        statusGroup.Margin = new Padding(0, 0, 0, 10);
        statusGroup.Padding = new Padding(6, 4, 6, 6);
        //
        // statusLayout
        //
        statusLayout.Name = "statusLayout";
        statusLayout.Dock = DockStyle.Top;
        statusLayout.ColumnCount = 2;
        statusLayout.RowCount = 4;
        statusLayout.AutoSize = true;
        statusLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        //
        // statusCaption0
        //
        statusCaption0.Name = "statusCaption0";
        statusCaption0.Text = "Windows Service:";
        statusCaption0.AutoSize = true;
        statusCaption0.Anchor = AnchorStyles.Left;
        statusCaption0.Margin = new Padding(0, 6, 8, 2);
        //
        // ServiceStatusText
        //
        ServiceStatusText.Name = "ServiceStatusText";
        ServiceStatusText.Text = "Đang kiểm tra…";
        ServiceStatusText.AutoSize = false;
        ServiceStatusText.Height = 20;
        ServiceStatusText.Dock = DockStyle.Fill;
        ServiceStatusText.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        ServiceStatusText.TextAlign = ContentAlignment.MiddleLeft;
        ServiceStatusText.Margin = new Padding(0, 4, 0, 2);
        //
        // statusCaption1
        //
        statusCaption1.Name = "statusCaption1";
        statusCaption1.Text = "Kết nối server:";
        statusCaption1.AutoSize = true;
        statusCaption1.Anchor = AnchorStyles.Left;
        statusCaption1.Margin = new Padding(0, 6, 8, 2);
        //
        // StatusText
        //
        StatusText.Name = "StatusText";
        StatusText.Text = "Chưa kết nối";
        StatusText.AutoSize = false;
        StatusText.Height = 20;
        StatusText.Dock = DockStyle.Fill;
        StatusText.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        StatusText.TextAlign = ContentAlignment.MiddleLeft;
        StatusText.Margin = new Padding(0, 4, 0, 2);
        //
        // statusCaption2
        //
        statusCaption2.Name = "statusCaption2";
        statusCaption2.Text = "Room:";
        statusCaption2.AutoSize = true;
        statusCaption2.Anchor = AnchorStyles.Left;
        statusCaption2.Margin = new Padding(0, 6, 8, 2);
        //
        // RoomText
        //
        RoomText.Name = "RoomText";
        RoomText.Text = "Chưa được gán";
        RoomText.AutoSize = false;
        RoomText.Height = 20;
        RoomText.Dock = DockStyle.Fill;
        RoomText.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        RoomText.TextAlign = ContentAlignment.MiddleLeft;
        RoomText.Margin = new Padding(0, 4, 0, 2);
        RoomText.AutoEllipsis = true;
        //
        // statusCaption3
        //
        statusCaption3.Name = "statusCaption3";
        statusCaption3.Text = "Tunnel:";
        statusCaption3.AutoSize = true;
        statusCaption3.Anchor = AnchorStyles.Left;
        statusCaption3.Margin = new Padding(0, 6, 8, 2);
        //
        // TunnelText
        //
        TunnelText.Name = "TunnelText";
        TunnelText.Text = "Chưa cấu hình";
        TunnelText.AutoSize = false;
        TunnelText.Height = 20;
        TunnelText.Dock = DockStyle.Fill;
        TunnelText.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        TunnelText.TextAlign = ContentAlignment.MiddleLeft;
        TunnelText.Margin = new Padding(0, 4, 0, 2);
        //
        // sharedGroup
        //
        sharedGroup.Name = "sharedGroup";
        sharedGroup.Text = "Máy in chia sẻ";
        sharedGroup.Dock = DockStyle.Fill;
        sharedGroup.Margin = new Padding(0, 0, 0, 10);
        sharedGroup.Padding = new Padding(6, 4, 6, 6);
        //
        // sharedLayout
        //
        sharedLayout.Name = "sharedLayout";
        sharedLayout.Dock = DockStyle.Fill;
        sharedLayout.ColumnCount = 1;
        sharedLayout.RowCount = 3;
        sharedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        sharedLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        sharedLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        sharedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31F));
        //
        // sharedHint
        //
        sharedHint.Name = "sharedHint";
        sharedHint.Text = "Chọn máy in để máy cùng Room sử dụng.";
        sharedHint.AutoSize = false;
        sharedHint.Height = 18;
        sharedHint.Dock = DockStyle.Fill;
        sharedHint.ForeColor = SystemColors.GrayText;
        sharedHint.Margin = new Padding(0, 0, 0, 6);
        //
        // PrintersList
        //
        PrintersList.Name = "PrintersList";
        PrintersList.CheckOnClick = true;
        PrintersList.IntegralHeight = false;
        PrintersList.BorderStyle = BorderStyle.FixedSingle;
        PrintersList.BackColor = SystemColors.Window;
        PrintersList.HorizontalScrollbar = true;
        PrintersList.Dock = DockStyle.Fill;
        PrintersList.FormattingEnabled = true;
        //
        // sharedBottom
        //
        sharedBottom.Name = "sharedBottom";
        sharedBottom.Dock = DockStyle.Fill;
        sharedBottom.ColumnCount = 2;
        sharedBottom.RowCount = 1;
        sharedBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        sharedBottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        sharedBottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        //
        // PrintersHintText
        //
        PrintersHintText.Name = "PrintersHintText";
        PrintersHintText.Text = "";
        PrintersHintText.AutoSize = false;
        PrintersHintText.Dock = DockStyle.Fill;
        PrintersHintText.ForeColor = SystemColors.GrayText;
        PrintersHintText.TextAlign = ContentAlignment.MiddleLeft;
        //
        // ScanPrintersButton
        //
        ScanPrintersButton.Name = "ScanPrintersButton";
        ScanPrintersButton.Text = "Quét lại";
        ScanPrintersButton.AutoSize = false;
        ScanPrintersButton.Size = new Size(88, 25);
        ScanPrintersButton.Margin = new Padding(0);
        ScanPrintersButton.UseVisualStyleBackColor = true;
        ScanPrintersButton.Anchor = AnchorStyles.Right;
        //
        // rightSplit
        //
        rightSplit.Name = "rightSplit";
        rightSplit.Dock = DockStyle.Fill;
        rightSplit.Orientation = Orientation.Horizontal;
        rightSplit.Size = new Size(468, 510);
        rightSplit.SplitterWidth = 6;
        rightSplit.SplitterDistance = 250;
        rightSplit.Panel1MinSize = 130;
        rightSplit.Panel2MinSize = 100;
        //
        // peersGroup
        //
        peersGroup.Name = "peersGroup";
        peersGroup.Text = "Máy cùng Room";
        peersGroup.Dock = DockStyle.Fill;
        peersGroup.Margin = new Padding(0, 0, 0, 10);
        peersGroup.Padding = new Padding(6, 4, 6, 6);
        //
        // peersLayout
        //
        peersLayout.Name = "peersLayout";
        peersLayout.Dock = DockStyle.Fill;
        peersLayout.ColumnCount = 1;
        peersLayout.RowCount = 3;
        peersLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        peersLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        peersLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        peersLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31F));
        //
        // PeersSummaryText
        //
        PeersSummaryText.Name = "PeersSummaryText";
        PeersSummaryText.Text = "Chỉ các máy online mới xuất hiện.";
        PeersSummaryText.AutoSize = false;
        PeersSummaryText.Height = 20;
        PeersSummaryText.Dock = DockStyle.Fill;
        PeersSummaryText.ForeColor = SystemColors.GrayText;
        PeersSummaryText.Margin = new Padding(0, 0, 0, 6);
        //
        // peersHost
        //
        peersHost.Name = "peersHost";
        peersHost.Dock = DockStyle.Fill;
        peersHost.BorderStyle = BorderStyle.FixedSingle;
        //
        // PeersGrid
        //
        PeersGrid.Name = "PeersGrid";
        PeersGrid.Dock = DockStyle.Fill;
        PeersGrid.BorderStyle = BorderStyle.None;
        PeersGrid.IntegralHeight = false;
        //
        // EmptyPeersPanel
        //
        EmptyPeersPanel.Name = "EmptyPeersPanel";
        EmptyPeersPanel.Text = "Chưa có máy khác online\r\nKết nối client trên máy khác và duyệt vào cùng Room để in từ xa.";
        EmptyPeersPanel.AutoSize = false;
        EmptyPeersPanel.Dock = DockStyle.Fill;
        EmptyPeersPanel.Visible = false;
        EmptyPeersPanel.TextAlign = ContentAlignment.MiddleCenter;
        EmptyPeersPanel.BackColor = SystemColors.Window;
        //
        // connectLayout
        //
        connectLayout.Name = "connectLayout";
        connectLayout.Dock = DockStyle.Fill;
        connectLayout.ColumnCount = 3;
        connectLayout.RowCount = 1;
        connectLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        connectLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        connectLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        connectLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        //
        // RemotePrinters
        //
        RemotePrinters.Name = "RemotePrinters";
        RemotePrinters.DropDownStyle = ComboBoxStyle.DropDownList;
        RemotePrinters.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        RemotePrinters.Margin = new Padding(0, 0, 6, 0);
        //
        // ConnectPrinterButton
        //
        ConnectPrinterButton.Name = "ConnectPrinterButton";
        ConnectPrinterButton.Text = "Kết nối máy in";
        ConnectPrinterButton.AutoSize = false;
        ConnectPrinterButton.Size = new Size(128, 25);
        ConnectPrinterButton.Margin = new Padding(0, 0, 6, 0);
        ConnectPrinterButton.UseVisualStyleBackColor = true;
        ConnectPrinterButton.Anchor = AnchorStyles.Left;
        ConnectPrinterButton.Enabled = false;
        //
        // PrintTestButton
        //
        PrintTestButton.Name = "PrintTestButton";
        PrintTestButton.Text = "In thử";
        PrintTestButton.AutoSize = false;
        PrintTestButton.Size = new Size(72, 25);
        PrintTestButton.Margin = new Padding(0);
        PrintTestButton.UseVisualStyleBackColor = true;
        PrintTestButton.Anchor = AnchorStyles.Left;
        PrintTestButton.Enabled = false;
        //
        // activityGroup
        //
        activityGroup.Name = "activityGroup";
        activityGroup.Text = "Hoạt động gần đây";
        activityGroup.Dock = DockStyle.Fill;
        activityGroup.Margin = new Padding(0, 0, 0, 10);
        activityGroup.Padding = new Padding(6, 4, 6, 6);
        //
        // activityLayout
        //
        activityLayout.Name = "activityLayout";
        activityLayout.Dock = DockStyle.Fill;
        activityLayout.ColumnCount = 1;
        activityLayout.RowCount = 2;
        activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31F));
        //
        // ActivityList
        //
        ActivityList.Name = "ActivityList";
        ActivityList.Font = new Font("Segoe UI", 8.25F);
        ActivityList.Dock = DockStyle.Fill;
        ActivityList.BorderStyle = BorderStyle.FixedSingle;
        ActivityList.IntegralHeight = false;
        //
        // OpenTestFilesButton
        //
        OpenTestFilesButton.Name = "OpenTestFilesButton";
        OpenTestFilesButton.Text = "Mở file in thử…";
        OpenTestFilesButton.AutoSize = false;
        OpenTestFilesButton.Size = new Size(150, 25);
        OpenTestFilesButton.Margin = new Padding(0, 0, 6, 0);
        OpenTestFilesButton.UseVisualStyleBackColor = true;
        OpenTestFilesButton.Anchor = AnchorStyles.Right;
        OpenTestFilesButton.Enabled = false;
        OpenTestFilesButton.Visible = false;
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        ClientSize = new Size(820, 560);
        Name = "MainForm";
        Text = "Remote Print";
        MinimumSize = new Size(700, 470);
        StartPosition = FormStartPosition.CenterScreen;
        MainMenuStrip = _menu;
        statusLayout.Controls.Add(statusCaption0, 0, 0);
        statusLayout.Controls.Add(ServiceStatusText, 1, 0);
        statusLayout.Controls.Add(statusCaption1, 0, 1);
        statusLayout.Controls.Add(StatusText, 1, 1);
        statusLayout.Controls.Add(statusCaption2, 0, 2);
        statusLayout.Controls.Add(RoomText, 1, 2);
        statusLayout.Controls.Add(statusCaption3, 0, 3);
        statusLayout.Controls.Add(TunnelText, 1, 3);
        statusGroup.Controls.Add(statusLayout);
        leftColumn.Controls.Add(statusGroup, 0, 0);
        sharedBottom.Controls.Add(PrintersHintText, 0, 0);
        sharedBottom.Controls.Add(ScanPrintersButton, 1, 0);
        sharedLayout.Controls.Add(sharedHint, 0, 0);
        sharedLayout.Controls.Add(PrintersList, 0, 1);
        sharedLayout.Controls.Add(sharedBottom, 0, 2);
        sharedGroup.Controls.Add(sharedLayout);
        leftColumn.Controls.Add(sharedGroup, 0, 1);
        peersHost.Controls.Add(PeersGrid);
        peersHost.Controls.Add(EmptyPeersPanel);
        connectLayout.Controls.Add(RemotePrinters, 0, 0);
        connectLayout.Controls.Add(ConnectPrinterButton, 1, 0);
        connectLayout.Controls.Add(PrintTestButton, 2, 0);
        peersLayout.Controls.Add(PeersSummaryText, 0, 0);
        peersLayout.Controls.Add(peersHost, 0, 1);
        peersLayout.Controls.Add(connectLayout, 0, 2);
        peersGroup.Controls.Add(peersLayout);
        rightSplit.Panel1.Controls.Add(peersGroup);
        activityLayout.Controls.Add(ActivityList, 0, 0);
        activityLayout.Controls.Add(OpenTestFilesButton, 0, 1);
        activityGroup.Controls.Add(activityLayout);
        rightSplit.Panel2.Controls.Add(activityGroup);
        bodyLayout.Controls.Add(leftColumn, 0, 0);
        bodyLayout.Controls.Add(rightSplit, 2, 0);
        rootLayout.Controls.Add(bodyLayout, 0, 0);
        this.Controls.Add(rootLayout);
        this.Controls.Add(_menu);
        activityLayout.ResumeLayout(false);
        activityLayout.PerformLayout();
        activityGroup.ResumeLayout(false);
        connectLayout.ResumeLayout(false);
        connectLayout.PerformLayout();
        peersHost.ResumeLayout(false);
        peersLayout.ResumeLayout(false);
        peersLayout.PerformLayout();
        peersGroup.ResumeLayout(false);
        rightSplit.Panel1.ResumeLayout(false);
        rightSplit.Panel2.ResumeLayout(false);
        ((ISupportInitialize)rightSplit).EndInit();
        rightSplit.ResumeLayout(false);
        sharedBottom.ResumeLayout(false);
        sharedBottom.PerformLayout();
        sharedLayout.ResumeLayout(false);
        sharedLayout.PerformLayout();
        sharedGroup.ResumeLayout(false);
        statusLayout.ResumeLayout(false);
        statusLayout.PerformLayout();
        statusGroup.ResumeLayout(false);
        leftColumn.ResumeLayout(false);
        leftColumn.PerformLayout();
        bodyLayout.ResumeLayout(false);
        bodyLayout.PerformLayout();
        rootLayout.ResumeLayout(false);
        rootLayout.PerformLayout();
        _menu.ResumeLayout(false);
        _menu.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private System.Windows.Forms.Timer _timer;
    private MenuStrip _menu;
    private ToolStripMenuItem _settingsItem;
    private ToolStripMenuItem _configureItem;
    internal ToolStripMenuItem _adminStartupItem;
    private ToolStripMenuItem _checkUpdateItem;
    private ToolStripMenuItem _serviceInstallItem;
    private ToolStripMenuItem _serviceUninstallItem;
    private ToolStripMenuItem _clientUninstallItem;
    private ToolStripMenuItem _testPrinterInstallItem;
    private ToolStripMenuItem _testPrinterUninstallItem;
    internal ToolStripMenuItem InstalledPrintersItem;
    private ToolStripMenuItem _clearTestFilesItem;
    private ToolStripMenuItem _languageItem;
    private ToolStripMenuItem _vietnameseItem;
    private ToolStripMenuItem _englishItem;
    private ToolStripSeparator _setupSeparator;
    private ToolStripSeparator _printerSeparator;
    private TableLayoutPanel rootLayout;
    private TableLayoutPanel bodyLayout;
    private TableLayoutPanel leftColumn;
    private GroupBox statusGroup;
    private TableLayoutPanel statusLayout;
    private Label statusCaption0;
    public Label ServiceStatusText;
    private Label statusCaption1;
    public Label StatusText;
    private Label statusCaption2;
    public Label RoomText;
    private Label statusCaption3;
    public Label TunnelText;
    private GroupBox sharedGroup;
    private TableLayoutPanel sharedLayout;
    private Label sharedHint;
    public CheckedListBox PrintersList;
    private TableLayoutPanel sharedBottom;
    public Label PrintersHintText;
    public Button ScanPrintersButton;
    private SplitContainer rightSplit;
    private GroupBox peersGroup;
    private TableLayoutPanel peersLayout;
    public Label PeersSummaryText;
    private Panel peersHost;
    public PeerListBox PeersGrid;
    public Label EmptyPeersPanel;
    private TableLayoutPanel connectLayout;
    public ComboBox RemotePrinters;
    public Button ConnectPrinterButton;
    public Button PrintTestButton;
    private GroupBox activityGroup;
    private TableLayoutPanel activityLayout;
    public WrappedListBox ActivityList;
    public Button OpenTestFilesButton;
}
