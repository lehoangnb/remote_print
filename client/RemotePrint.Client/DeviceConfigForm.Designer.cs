// Layout and control properties are maintained by the Windows Forms Designer.
#nullable disable
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemotePrint.Client;

partial class DeviceConfigForm
{
    private IContainer components;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new Container();
        configLayout = new TableLayoutPanel();
        caption0 = new Label();
        _name = new TextBox();
        caption1 = new Label();
        _url = new TextBox();
        pairingToolTip = new ToolTip(components);
        pairingHint = new Label();
        buttonsLayout = new TableLayoutPanel();
        SaveButton = new Button();
        SkipButton = new Button();
        configLayout.SuspendLayout();
        buttonsLayout.SuspendLayout();
        SuspendLayout();
        //
        // configLayout
        //
        configLayout.Name = "configLayout";
        configLayout.Dock = DockStyle.Fill;
        configLayout.ColumnCount = 2;
        configLayout.RowCount = 4;
        configLayout.Padding = new Padding(12);
        configLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        configLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        configLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        configLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        configLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        configLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        //
        // caption0
        //
        caption0.Name = "caption0";
        caption0.Text = "Tên máy tính:";
        caption0.AutoSize = true;
        caption0.Anchor = AnchorStyles.Left;
        caption0.Margin = new Padding(0, 6, 8, 2);
        //
        // _name
        //
        _name.Name = "_name";
        _name.MaxLength = 80;
        _name.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _name.Height = 23;
        _name.Margin = new Padding(0, 3, 0, 6);
        //
        // caption1
        //
        caption1.Name = "caption1";
        caption1.Text = "Địa chỉ server:";
        caption1.AutoSize = true;
        caption1.Anchor = AnchorStyles.Left;
        caption1.Margin = new Padding(0, 6, 8, 2);
        //
        // _url
        //
        _url.Name = "_url";
        _url.MaxLength = 4096;
        _url.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _url.Height = 23;
        _url.Margin = new Padding(0, 3, 0, 6);
        //
        // pairingToolTip
        //
        pairingToolTip.SetToolTip(_url, "Dán mã RPPAIR1. do admin tạo (có hiệu lực 5 phút). Máy đã ghép nối dùng địa chỉ server hiện có.");
        //
        // pairingHint
        //
        pairingHint.Name = "pairingHint";
        pairingHint.Text = "Máy mới: dán mã ghép nối 5 phút từ admin.";
        pairingHint.AutoSize = false;
        pairingHint.Height = 20;
        pairingHint.Dock = DockStyle.Fill;
        pairingHint.ForeColor = SystemColors.GrayText;
        pairingHint.Margin = new Padding(0, 0, 0, 6);
        //
        // buttonsLayout
        //
        buttonsLayout.Name = "buttonsLayout";
        buttonsLayout.Dock = DockStyle.Right;
        buttonsLayout.ColumnCount = 2;
        buttonsLayout.RowCount = 1;
        buttonsLayout.AutoSize = true;
        buttonsLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        buttonsLayout.Margin = new Padding(0, 6, 0, 0);
        buttonsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        buttonsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        buttonsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        //
        // SaveButton
        //
        SaveButton.Name = "SaveButton";
        SaveButton.Text = "Lưu và kết nối";
        SaveButton.AutoSize = false;
        SaveButton.Size = new Size(128, 25);
        SaveButton.Margin = new Padding(0, 0, 6, 0);
        SaveButton.UseVisualStyleBackColor = true;
        SaveButton.DialogResult = DialogResult.OK;
        //
        // SkipButton
        //
        SkipButton.Name = "SkipButton";
        SkipButton.Text = "Bỏ qua";
        SkipButton.AutoSize = false;
        SkipButton.Size = new Size(128, 25);
        SkipButton.Margin = new Padding(0);
        SkipButton.UseVisualStyleBackColor = true;
        SkipButton.DialogResult = DialogResult.Cancel;
        //
        // DeviceConfigForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        ClientSize = new Size(470, 158);
        Name = "DeviceConfigForm";
        Text = "Cấu hình máy và server";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AcceptButton = SaveButton;
        CancelButton = SkipButton;
        configLayout.Controls.Add(caption0, 0, 0);
        configLayout.Controls.Add(_name, 1, 0);
        configLayout.Controls.Add(caption1, 0, 1);
        configLayout.Controls.Add(_url, 1, 1);
        buttonsLayout.Controls.Add(SaveButton, 0, 0);
        buttonsLayout.Controls.Add(SkipButton, 1, 0);
        configLayout.Controls.Add(pairingHint, 1, 2);
        configLayout.Controls.Add(buttonsLayout, 1, 3);
        this.Controls.Add(configLayout);
        buttonsLayout.ResumeLayout(false);
        buttonsLayout.PerformLayout();
        configLayout.ResumeLayout(false);
        configLayout.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private TableLayoutPanel configLayout;
    private Label caption0;
    private TextBox _name;
    private Label caption1;
    private TextBox _url;
    private ToolTip pairingToolTip;
    private Label pairingHint;
    private TableLayoutPanel buttonsLayout;
    public Button SaveButton;
    public Button SkipButton;
}
