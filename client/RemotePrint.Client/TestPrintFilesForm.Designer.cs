// Layout and control properties are maintained by the Windows Forms Designer.
#nullable disable
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace RemotePrint.Client;

partial class TestPrintFilesForm
{
    private IContainer components;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!IsDisposed)
            {
                _closing.Cancel();
                _closing.Dispose();
            }
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new Container();
        mainLayout = new TableLayoutPanel();
        _files = new ListView();
        receivedColumn = new ColumnHeader();
        bytesColumn = new ColumnHeader();
        formatColumn = new ColumnHeader();
        nameColumn = new ColumnHeader();
        _open = new Button();
        _clear = new Button();
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
        // _files
        //
        _files.Name = "_files";
        _files.Dock = DockStyle.Fill;
        _files.View = View.Details;
        _files.FullRowSelect = true;
        _files.MultiSelect = false;
        _files.HideSelection = false;
        _files.Columns.AddRange(new ColumnHeader[] { receivedColumn, bytesColumn, formatColumn, nameColumn });
        //
        // receivedColumn
        //
        receivedColumn.Name = "receivedColumn";
        receivedColumn.Text = "Thời gian nhận";
        receivedColumn.Width = 160;
        //
        // bytesColumn
        //
        bytesColumn.Name = "bytesColumn";
        bytesColumn.Text = "Kích thước (byte)";
        bytesColumn.Width = 115;
        bytesColumn.TextAlign = HorizontalAlignment.Right;
        //
        // formatColumn
        //
        formatColumn.Name = "formatColumn";
        formatColumn.Text = "Định dạng";
        formatColumn.Width = 85;
        //
        // nameColumn
        //
        nameColumn.Name = "nameColumn";
        nameColumn.Text = "Tên file";
        nameColumn.Width = 250;
        //
        // _open
        //
        _open.Name = "_open";
        _open.Text = "Mở file";
        _open.AutoSize = false;
        _open.Size = new Size(128, 25);
        _open.Margin = new Padding(0, 0, 6, 0);
        _open.UseVisualStyleBackColor = true;
        _open.Enabled = false;
        //
        // _clear
        //
        _clear.Name = "_clear";
        _clear.Text = "Dọn tất cả";
        _clear.AutoSize = false;
        _clear.Size = new Size(128, 25);
        _clear.Margin = new Padding(0, 0, 6, 0);
        _clear.UseVisualStyleBackColor = true;
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
        // TestPrintFilesForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        ClientSize = new Size(660, 340);
        Name = "TestPrintFilesForm";
        Text = "File in thử đã nhận";
        MinimumSize = new Size(600, 280);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        CancelButton = closeButton;
        AcceptButton = _open;
        buttonsLayout.Controls.Add(closeButton);
        buttonsLayout.Controls.Add(_open);
        buttonsLayout.Controls.Add(_refresh);
        buttonsLayout.Controls.Add(_clear);
        mainLayout.Controls.Add(_files, 0, 0);
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
    private ListView _files;
    private ColumnHeader receivedColumn;
    private ColumnHeader bytesColumn;
    private ColumnHeader formatColumn;
    private ColumnHeader nameColumn;
    private Button _open;
    private Button _clear;
    private Label _status;
    private Button _refresh;
    private Button closeButton;
    private FlowLayoutPanel buttonsLayout;
}
