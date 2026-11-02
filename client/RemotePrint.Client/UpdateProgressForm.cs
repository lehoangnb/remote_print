namespace RemotePrint.Client;

public sealed partial class UpdateProgressForm : Form
{
    public UpdateProgressForm()
    {
        InitializeComponent();
        if (UiHelpers.IsDesignMode(this)) return;
        Icon = Program.AppIcon;
        L.Apply(this);
        cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    internal event EventHandler? CancelRequested;

    internal void ReportProgress(int value)
    {
        if (!IsDisposed) progressBar.Value = Math.Clamp(value, progressBar.Minimum, progressBar.Maximum);
    }

    internal void ShowVerification() => statusLabel.Text = L.T("Đang kiểm tra gói cập nhật…");

    internal void ShowInstallation(bool requiresElevation = true)
    {
        statusLabel.Text = L.T(requiresElevation ? "Đang cài đặt; hãy xác nhận yêu cầu quyền quản trị…" : "Đang cài đặt bản cập nhật…");
        cancelButton.Enabled = false;
    }
}
