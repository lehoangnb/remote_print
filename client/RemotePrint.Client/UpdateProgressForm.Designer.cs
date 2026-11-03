// Layout and control properties are maintained by the Windows Forms Designer.
#nullable disable
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemotePrint.Client;

partial class UpdateProgressForm
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
        progressBar = new ProgressBar();
        statusLabel = new Label();
        cancelButton = new Button();
        layoutPanel = new TableLayoutPanel();
        buttonPanel = new FlowLayoutPanel();
        layoutPanel.SuspendLayout();
        buttonPanel.SuspendLayout();
        SuspendLayout();
        //
        // progressBar
        //
        progressBar.Name = "progressBar";
        progressBar.Dock = DockStyle.Fill;
        progressBar.Margin = new Padding(0, 4, 0, 8);
        //
        // statusLabel
        //
        statusLabel.Name = "statusLabel";
        statusLabel.Text = "Đang tải bản cập nhật…";
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.Margin = new Padding(0);
        //
        // cancelButton
        //
        cancelButton.Name = "cancelButton";
        cancelButton.Text = "Hủy";
        cancelButton.Size = new Size(96, 32);
        cancelButton.Margin = new Padding(0);
        cancelButton.UseVisualStyleBackColor = true;
        //
        // layoutPanel
        //
        layoutPanel.Name = "layoutPanel";
        layoutPanel.Dock = DockStyle.Fill;
        layoutPanel.Padding = new Padding(20, 12, 20, 14);
        layoutPanel.ColumnCount = 1;
        layoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layoutPanel.RowCount = 3;
        layoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        layoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        layoutPanel.Controls.Add(statusLabel, 0, 0);
        layoutPanel.Controls.Add(progressBar, 0, 1);
        layoutPanel.Controls.Add(buttonPanel, 0, 2);
        //
        // buttonPanel
        //
        buttonPanel.Name = "buttonPanel";
        buttonPanel.Dock = DockStyle.Fill;
        buttonPanel.Margin = new Padding(0);
        buttonPanel.FlowDirection = FlowDirection.RightToLeft;
        buttonPanel.Controls.Add(cancelButton);
        //
        // UpdateProgressForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        ClientSize = new Size(440, 150);
        Name = "UpdateProgressForm";
        Text = "Cập nhật ứng dụng";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(layoutPanel);
        buttonPanel.ResumeLayout(false);
        layoutPanel.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private ProgressBar progressBar;
    private Label statusLabel;
    private Button cancelButton;
    private TableLayoutPanel layoutPanel;
    private FlowLayoutPanel buttonPanel;
}
