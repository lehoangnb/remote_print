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
        Shown += (_, _) => CenterOnOwner();
    }

    private void CenterOnOwner()
    {
        if (StartPosition == FormStartPosition.Manual) return;
        var owner = Owner;
        var area = Screen.FromControl(owner is { Visible: true } ? owner : this).WorkingArea;
        var reference = owner is { Visible: true, WindowState: not FormWindowState.Minimized } ? owner.Bounds : area;
        var x = reference.Left + (reference.Width - Width) / 2;
        var y = reference.Top + (reference.Height - Height) / 2;
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    internal event EventHandler? CancelRequested;

    internal void ReportProgress(int value)
    {
        if (!IsDisposed) progressBar.Value = Math.Clamp(value, progressBar.Minimum, progressBar.Maximum);
    }

    internal void ShowVerification() => statusLabel.Text = L.T("Đang kiểm tra gói cập nhật…");

    private void ShowOperation(string title, string status)
    {
        Text = L.T(title);
        statusLabel.Text = L.T(status);
        progressBar.Style = ProgressBarStyle.Marquee;
        cancelButton.Visible = false;
    }

    internal void ShowPrinterInstallation() => ShowOperation("Cài đặt máy in", "Đang cài đặt máy in, vui lòng chờ…");
    internal void ShowPrinterRemoval() => ShowOperation("Xóa máy in", "Đang xóa máy in, vui lòng chờ…");
    internal void ShowServiceInstallation() => ShowOperation("Cài đặt dịch vụ", "Đang cài đặt dịch vụ; hãy xác nhận yêu cầu quyền quản trị nếu có…");
    internal void ShowServiceRemoval() => ShowOperation("Gỡ cài đặt dịch vụ", "Đang gỡ dịch vụ; hãy xác nhận yêu cầu quyền quản trị nếu có…");
    internal void ShowClientRemoval() => ShowOperation("Gỡ cài đặt ứng dụng", "Đang gỡ ứng dụng; hãy xác nhận yêu cầu quyền quản trị nếu có…");

    internal void ShowInstallation(bool requiresElevation = true)
    {
        statusLabel.Text = L.T(requiresElevation ? "Đang cài đặt; hãy xác nhận yêu cầu quyền quản trị…" : "Đang cài đặt bản cập nhật…");
        cancelButton.Enabled = false;
    }
}
