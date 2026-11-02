using System.Drawing;
using System.Windows.Forms;

namespace RemotePrint.Client;

// Machine setup is a once-per-install task, so it lives behind the Cài đặt menu and
// only these two small windows appear. Plain WinForms keeps the native look and the
// captions of the buttons, which MessageBox cannot rename.
public sealed class DeviceConfigForm : Form
{
    private readonly TextBox _name = new() { MaxLength = 80 };
    private readonly TextBox _url = new TextBox { MaxLength = 4096 }.SetTip(L.T("Dán mã RPPAIR1. do admin tạo (có hiệu lực 5 phút). Máy đã ghép nối dùng địa chỉ server hiện có."));
    public Button SaveButton { get; } = MainForm.NewButton(L.T("Lưu và kết nối"));
    public Button SkipButton { get; } = MainForm.NewButton(L.T("Bỏ qua"));

    public string ComputerName => _name.Text.Trim();
    public string ServerUrl => _url.Text.Trim();

    public DeviceConfigForm(string computerName, string serverUrl, bool headless = false)
    {
        Text = L.T("Cấu hình máy và server");
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(470, 158);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4 };
        grid.ColumnStyles.AddRange([
            new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100F)
        ]);
        grid.RowStyles.AddRange([
            new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize),
            new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize)
        ]);
        grid.Controls.Add(new Label
        {
            Text = L.T("Tên máy tính:"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 2)
        }, 0, 0);
        _name.Text = computerName;
        BindBox(_name, 0);
        grid.Controls.Add(_name, 1, 0);
        grid.Controls.Add(new Label
        {
            Text = L.T("Địa chỉ server:"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 2)
        }, 0, 1);
        _url.Text = serverUrl;
        BindBox(_url, 1);
        grid.Controls.Add(_url, 1, 1);
        grid.Controls.Add(new Label
        {
            Text = L.T("Máy mới: dán mã ghép nối 5 phút từ admin."), AutoSize = false, Height = 20,
            Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6)
        }, 1, 2);
        grid.Controls.Add(BuildButtons(), 1, 3);
        Controls.Add(grid);

        AcceptButton = SaveButton;
        CancelButton = SkipButton;
        ActiveControl = _url;
        if (headless)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-2400, -2400);
            ShowInTaskbar = false;
        }
    }

    private void BindBox(TextBox box, int row)
    {
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        box.Height = 23;
        box.Margin = new Padding(0, 3, 0, 6);
    }

    private Control BuildButtons()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 6, 0, 0)
        };
        panel.ColumnStyles.AddRange([
            new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.AutoSize)
        ]);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        SaveButton.DialogResult = DialogResult.OK;
        SkipButton.DialogResult = DialogResult.Cancel;
        SkipButton.Margin = new Padding(0);
        panel.Controls.Add(SaveButton, 0, 0);
        panel.Controls.Add(SkipButton, 1, 0);
        return panel;
    }
}

// Shown the first time the window finds no Windows Service: the interface alone
// cannot receive jobs, and installing needs admin rights the user should expect.
public sealed class SetupNoticeForm : Form
{
    public Button InstallButton { get; } = MainForm.NewButton(L.T("Cài dịch vụ"));
    public Button SkipButton { get; } = MainForm.NewButton(L.T("Bỏ qua"));

    public SetupNoticeForm(bool headless = false)
    {
        Text = L.T("Cài Windows Service");
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 132);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.RowStyles.AddRange([
            new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.Percent, 100F), new RowStyle(SizeType.AutoSize)
        ]);
        grid.Controls.Add(new Label
        {
            Text = L.T("Chưa cài Windows Service"), AutoSize = true, Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        grid.Controls.Add(new Label
        {
            Text = L.T("Máy khác chỉ in tới máy này khi có Windows Service.\nCài đặt cần quyền quản trị."),
            AutoSize = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10)
        }, 0, 1);
        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        buttons.ColumnStyles.AddRange([
            new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.AutoSize)
        ]);
        buttons.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        InstallButton.DialogResult = DialogResult.OK;
        SkipButton.DialogResult = DialogResult.Cancel;
        SkipButton.Margin = new Padding(0);
        buttons.Controls.Add(InstallButton, 0, 0);
        buttons.Controls.Add(SkipButton, 1, 0);
        grid.Controls.Add(buttons, 0, 2);
        Controls.Add(grid);

        AcceptButton = InstallButton;
        CancelButton = SkipButton;
        if (headless)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-2400, -2400);
            ShowInTaskbar = false;
        }
    }
}
