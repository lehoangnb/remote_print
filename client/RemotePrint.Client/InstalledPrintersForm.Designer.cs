// Layout and control properties are maintained by the Windows Forms Designer.
#nullable disable
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemotePrint.Client;

partial class InstalledPrintersForm
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
        mainLayout = new TableLayoutPanel();
        Printers = new ListBox();
        RemoveButton = new Button();
        _status = new Label();
        _refresh = new Button();
        closeButton = new Button();
        buttonsLayout = new FlowLayoutPanel();
        mainLayout.SuspendLayout();
        buttonsLayout.SuspendLayout();
        SuspendLayout();
        //
        // mainLayout
        //
        mainLayout.Name = "mainLayout";
        mainLayout.Dock = DockStyle.Fill;
        mainLayout.ColumnCount = 1;
        mainLayout.RowCount = 3;
        mainLayout.Padding = new Padding(12);
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        //
        // Printers
        //
        Printers.Name = "Printers";
        Printers.Dock = DockStyle.Fill;
        Printers.DisplayMember = "Name";
        Printers.HorizontalScrollbar = true;
        //
        // RemoveButton
        //
        RemoveButton.Name = "RemoveButton";
        RemoveButton.Text = "Xóa máy in";
        RemoveButton.AutoSize = false;
        RemoveButton.Size = new Size(128, 25);
        RemoveButton.Margin = new Padding(0, 0, 6, 0);
        RemoveButton.UseVisualStyleBackColor = true;
        RemoveButton.Enabled = false;
        //
        // _status
        //
        _status.Name = "_status";
        _status.Dock = DockStyle.Fill;
        _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        //
        // _refresh
        //
        _refresh.Name = "_refresh";
        _refresh.Text = "Làm mới";
        _refresh.AutoSize = false;
        _refresh.Size = new Size(128, 25);
        _refresh.Margin = new Padding(0, 0, 6, 0);
        _refresh.UseVisualStyleBackColor = true;
        //
        // closeButton
        //
        closeButton.Name = "closeButton";
        closeButton.Text = "Đóng";
        closeButton.AutoSize = false;
        closeButton.Size = new Size(128, 25);
        closeButton.Margin = new Padding(0, 0, 6, 0);
        closeButton.UseVisualStyleBackColor = true;
        closeButton.DialogResult = DialogResult.Cancel;
        //
        // buttonsLayout
        //
        buttonsLayout.Name = "buttonsLayout";
        buttonsLayout.Dock = DockStyle.Fill;
        buttonsLayout.FlowDirection = FlowDirection.RightToLeft;
        //
        // InstalledPrintersForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        ClientSize = new Size(560, 300);
        Name = "InstalledPrintersForm";
        Text = "Máy in đã cài";
        MinimumSize = new Size(480, 260);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        CancelButton = closeButton;
        buttonsLayout.Controls.Add(closeButton);
        buttonsLayout.Controls.Add(RemoveButton);
        buttonsLayout.Controls.Add(_refresh);
        mainLayout.Controls.Add(Printers, 0, 0);
        mainLayout.Controls.Add(_status, 0, 1);
        mainLayout.Controls.Add(buttonsLayout, 0, 2);
        this.Controls.Add(mainLayout);
        buttonsLayout.ResumeLayout(false);
        mainLayout.ResumeLayout(false);
        mainLayout.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private TableLayoutPanel mainLayout;
    internal ListBox Printers;
    internal Button RemoveButton;
    private Label _status;
    private Button _refresh;
    private Button closeButton;
    private FlowLayoutPanel buttonsLayout;
}
