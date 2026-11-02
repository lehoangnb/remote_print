using System.Diagnostics;
using System.Windows.Forms;

namespace RemotePrint.Client;

public sealed partial class MainForm
{
    internal readonly ToolStripMenuItem _checkUpdateItem = new(L.T("Kiểm tra cập nhật…"));
    private DateTime _nextUpdateCheck = DateTime.MinValue;
    private string _updateServer = "";
    private readonly HashSet<string> _notifiedUpdates = [];
    private bool _updateBusy;
    private CancellationTokenSource? _updateCancellation;

    private async Task CheckScheduledUpdateAsync()
    {
        if (_headless || !_settingsLoaded || string.IsNullOrWhiteSpace(_serverUrl) || _setupPromptOpen || _updateBusy) return;
        if (_updateServer != _serverUrl) { _updateServer = _serverUrl; _nextUpdateCheck = DateTime.MinValue; }
        if (DateTime.UtcNow < _nextUpdateCheck) return;
        _nextUpdateCheck = DateTime.UtcNow.AddHours(6);
        await CheckForUpdateAsync(manual: false);
    }

    internal async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateBusy || _savingSettings || !_serviceInstallItem.Enabled) return;
        _updateBusy = true;
        _checkUpdateItem.Enabled = false;
        _updateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = _updateCancellation.Token;
        string? staging = null;
        try
        {
            using var updater = new ClientUpdater();
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            var release = await updater.CheckAsync(_serverUrl, checkTimeout.Token);
            if (release is null)
            {
                if (manual) MessageBox.Show(this, L.T("Không có bản cập nhật mới trên server.") + "\n\n" + L.T("Phiên bản hiện tại: ") + ClientUpdater.CurrentVersion,
                    L.T("Cập nhật ứng dụng"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var identity = _serverUrl + "/" + release.Sha256;
            if (!manual && !_notifiedUpdates.Add(identity)) return;
            Show(); WindowState = FormWindowState.Normal; Activate();
            var message = L.T("Có bản cập nhật mới: ") + release.Version + "\n" + L.T("Phiên bản hiện tại: ") + ClientUpdater.CurrentVersion +
                $"\n\n{release.Notes}\n\n" + L.T("Cập nhật sẽ khởi động lại ứng dụng và dịch vụ, tạm ngắt kết nối in. Cấu hình được giữ lại. Bạn có muốn cập nhật ngay?");
            if (MessageBox.Show(this, message, L.T("Cập nhật ứng dụng"), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePrint", "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, "package.zip");
            var package = Path.Combine(staging, "package");
            using var progressForm = new Form { Text = L.T("Cập nhật ứng dụng"), Width = 430, Height = 160,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ControlBox = false,
                StartPosition = FormStartPosition.CenterParent, Font = Font, Icon = Program.AppIcon };
            var label = new Label { Text = L.T("Đang tải bản cập nhật…"), Dock = DockStyle.Top, Height = 36, Padding = new Padding(12, 8, 0, 0) };
            var progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 22 };
            var cancel = new Button { Text = L.T("Hủy"), Dock = DockStyle.Bottom, Height = 30 };
            cancel.Click += (_, _) => _updateCancellation?.Cancel();
            progressForm.Controls.AddRange([progressBar, label, cancel]);
            progressForm.Show(this);
            Enabled = false;
            SetServiceItems(false);
            try
            {
                await updater.DownloadAsync(_serverUrl, release, archive, new Progress<int>(value => { if (!progressBar.IsDisposed) progressBar.Value = value; }), ct);
                label.Text = L.T("Đang kiểm tra gói cập nhật…");
                await Task.Run(() => ClientUpdater.ExtractAndVerify(archive, package, release, ct), ct);
                ct.ThrowIfCancellationRequested();
                label.Text = L.T("Đang cài đặt; hãy xác nhận yêu cầu quyền quản trị…");
                cancel.Enabled = false;
                var installed = await ElevatedSetup.RunAsync(SetupOperation.InstallService, null, Path.Combine(package, "RemotePrint.Client.exe"));
                if (string.IsNullOrWhiteSpace(installed) || !File.Exists(installed)) throw new InvalidOperationException(L.T("Không xác định được ứng dụng đã cập nhật để khởi động lại."));
                var restart = new ProcessStartInfo(installed) { UseShellExecute = false };
                restart.ArgumentList.Add("--wait-for-exit"); restart.ArgumentList.Add(Environment.ProcessId.ToString());
                using var restarted = Process.Start(restart) ?? throw new InvalidOperationException(L.T("Không xác định được ứng dụng đã cập nhật để khởi động lại."));
                StopAgent();
                Application.Exit();
            }
            finally { Enabled = true; progressForm.Close(); SetServiceItems(true); }
        }
        catch (OperationCanceledException)
        {
            if (manual && !IsDisposed) MessageBox.Show(this, L.T("Đã hủy hoặc hết thời gian cập nhật."), L.T("Cập nhật ứng dụng"));
        }
        catch (Exception ex)
        {
            // Background network checks stay quiet; a requested installation always reports failures.
            if (manual || staging is not null) ShowError(ex);
            else Debug.WriteLine("Update check: " + ex.Message);
        }
        finally
        {
            if (staging is not null)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
            }
            _updateCancellation.Dispose(); _updateCancellation = null;
            _updateBusy = false;
            _checkUpdateItem.Enabled = true;
        }
    }
}
