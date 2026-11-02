// Layout and control properties are maintained by the Windows Forms Designer.
#nullable disable
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemotePrint.Client;

partial class SetupNoticeForm
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
        noticeLayout = new TableLayoutPanel();
        headingLabel = new Label();
        descriptionLabel = new Label();
        buttonsLayout = new TableLayoutPanel();
        InstallButton = new Button();
        SkipButton = new Button();
        noticeLayout.SuspendLayout();
        buttonsLayout.SuspendLayout();
        SuspendLayout();
        //
        // noticeLayout
        //
        noticeLayout.Name = "noticeLayout";
        noticeLayout.Dock = DockStyle.Fill;
        noticeLayout.ColumnCount = 1;
        noticeLayout.RowCount = 3;
        noticeLayout.Padding = new Padding(12);
        noticeLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        noticeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        noticeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        noticeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        //
        // headingLabel
        //
        headingLabel.Name = "headingLabel";
        headingLabel.Text = "Chưa cài Windows Service";
        headingLabel.AutoSize = true;
        headingLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        headingLabel.Margin = new Padding(0, 0, 0, 8);
        //
        // descriptionLabel
        //
        descriptionLabel.Name = "descriptionLabel";
        descriptionLabel.Text = "Máy khác chỉ in tới máy này khi có Windows Service.\nCài đặt cần quyền quản trị.";
        descriptionLabel.AutoSize = false;
        descriptionLabel.Dock = DockStyle.Fill;
        descriptionLabel.Margin = new Padding(0, 0, 0, 10);
        //
        // buttonsLayout
        //
        buttonsLayout.Name = "buttonsLayout";
        buttonsLayout.Dock = DockStyle.Right;
        buttonsLayout.ColumnCount = 2;
        buttonsLayout.RowCount = 1;
        buttonsLayout.AutoSize = true;
        buttonsLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        buttonsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        buttonsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize, 0F));
        buttonsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0F));
        //
        // InstallButton
        //
        InstallButton.Name = "InstallButton";
        InstallButton.Text = "Cài dịch vụ";
        InstallButton.AutoSize = false;
        InstallButton.Size = new Size(128, 25);
        InstallButton.Margin = new Padding(0, 0, 6, 0);
        InstallButton.UseVisualStyleBackColor = true;
        InstallButton.DialogResult = DialogResult.OK;
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
        // SetupNoticeForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        ClientSize = new Size(430, 132);
        Name = "SetupNoticeForm";
        Text = "Cài Windows Service";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AcceptButton = InstallButton;
        CancelButton = SkipButton;
        buttonsLayout.Controls.Add(InstallButton, 0, 0);
        buttonsLayout.Controls.Add(SkipButton, 1, 0);
        noticeLayout.Controls.Add(headingLabel, 0, 0);
        noticeLayout.Controls.Add(descriptionLabel, 0, 1);
        noticeLayout.Controls.Add(buttonsLayout, 0, 2);
        this.Controls.Add(noticeLayout);
        buttonsLayout.ResumeLayout(false);
        buttonsLayout.PerformLayout();
        noticeLayout.ResumeLayout(false);
        noticeLayout.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private TableLayoutPanel noticeLayout;
    private Label headingLabel;
    private Label descriptionLabel;
    private TableLayoutPanel buttonsLayout;
    public Button InstallButton;
    public Button SkipButton;
}
