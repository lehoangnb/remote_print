using System.Diagnostics;
using System.Windows.Forms;

namespace RemotePrint.Client;

public sealed partial class MainForm
{
    private bool _updateBusy;
    private CancellationTokenSource? _updateCancellation;
    private readonly AutomaticUpdateAttempts _updateAttempts = new();
    private readonly ClientUpdatePolicy _updatePolicy = new();

    private async Task ApplyServerUpdateAsync()
    {
        if (_headless || !_settingsLoaded || _setupPromptOpen || _updateBusy || _savingSettings ||
            _printerDraftDirty || !_serviceInstallItem.Enabled || _snapshot?.Update is not { } command ||
            command.ServerUrl != _snapshot.Settings.ServerUrl ||
            !ClientUpdateProtocol.IsNewerRelease(command.Release)) return;
        var administrator = SetupPaths.IsAdministrator;
        var action = _updatePolicy.Decide(command, administrator, Visible && WindowState != FormWindowState.Minimized);
        if (action == ClientUpdateAction.None) return;
        var release = command.Release;
        _updateBusy = true;
        if (action == ClientUpdateAction.Notify)
        {
            try
            {
                var message = L.T("Có bản cập nhật mới: ") + release.Version + "\n\n" +
                    L.T("Cập nhật sẽ khởi động lại client và dịch vụ. Cài đặt ngay với quyền quản trị?");
                if (MessageBox.Show(this, message, L.T("Cập nhật ứng dụng"), MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            finally { _updateBusy = false; }
        }
        if (!_updateAttempts.TryBegin(command, DateTime.UtcNow, force: action == ClientUpdateAction.Notify)) { _updateBusy = false; return; }
        _updateBusy = true;
        _updateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = _updateCancellation.Token;
        string? staging = null;
        try
        {
            using var updater = new ClientUpdater();
            staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePrint", "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, "package.zip");
            var package = Path.Combine(staging, "package");
            using var progressForm = new UpdateProgressForm();
            progressForm.CancelRequested += (_, _) => _updateCancellation?.Cancel();
            progressForm.Show(this);
            Enabled = false;
            SetServiceItems(false);
            try
            {
                await updater.DownloadAsync(command.ServerUrl, release, archive, new Progress<int>(progressForm.ReportProgress), ct);
                progressForm.ShowVerification();
                await Task.Run(() => ClientUpdater.ExtractAndVerify(archive, package, release, ct), ct);
                ct.ThrowIfCancellationRequested();
                progressForm.ShowInstallation(requiresElevation: !administrator);
                var installed = await ElevatedSetup.RunAsync(SetupOperation.InstallService, null, Path.Combine(package, "RemotePrint.Client.exe"));
                if (string.IsNullOrWhiteSpace(installed) || !File.Exists(installed)) throw new InvalidOperationException(L.T("Không xác định được ứng dụng đã cập nhật để khởi động lại."));
                var restart = new ProcessStartInfo(installed) { UseShellExecute = false };
                if (!Visible) restart.ArgumentList.Add("--background");
                restart.ArgumentList.Add("--wait-for-exit"); restart.ArgumentList.Add(Environment.ProcessId.ToString());
                using var restarted = Process.Start(restart) ?? throw new InvalidOperationException(L.T("Không xác định được ứng dụng đã cập nhật để khởi động lại."));
                StopAgent();
                Application.Exit();
            }
            finally { Enabled = true; progressForm.Close(); SetServiceItems(true); }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && Visible) MessageBox.Show(this, L.T("Đã hủy hoặc hết thời gian cập nhật."), L.T("Cập nhật ứng dụng"));
        }
        catch (Exception ex)
        {
            if (!IsDisposed && Visible) ShowError(ex);
            else Debug.WriteLine(ex.Message);
        }
        finally
        {
            if (staging is not null)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
            }
            _updateCancellation?.Dispose(); _updateCancellation = null;
            _updateBusy = false;
        }
    }
}
