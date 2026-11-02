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
        SuspendLayout();
        //
        // progressBar
        //
        progressBar.Name = "progressBar";
        progressBar.Dock = DockStyle.Top;
        progressBar.Height = 22;
        //
        // statusLabel
        //
        statusLabel.Name = "statusLabel";
        statusLabel.Text = "Đang tải bản cập nhật…";
        statusLabel.Dock = DockStyle.Top;
        statusLabel.Height = 36;
        statusLabel.Padding = new Padding(12, 8, 0, 0);
        //
        // cancelButton
        //
        cancelButton.Name = "cancelButton";
        cancelButton.Text = "Hủy";
        cancelButton.Dock = DockStyle.Bottom;
        cancelButton.Height = 30;
        cancelButton.UseVisualStyleBackColor = true;
        //
        // UpdateProgressForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        ClientSize = new Size(414, 121);
        Name = "UpdateProgressForm";
        Text = "Cập nhật ứng dụng";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterParent;
        this.Controls.Add(progressBar);
        this.Controls.Add(statusLabel);
        this.Controls.Add(cancelButton);
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private ProgressBar progressBar;
    private Label statusLabel;
    private Button cancelButton;
}
