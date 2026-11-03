using System.Diagnostics;
using System.Windows.Forms;

namespace RemotePrint.Client;

public sealed partial class MainForm
{
    private bool _updateBusy;
    private bool _checkingClientUpdate;
    private CancellationTokenSource? _updateCancellation;
    private readonly AutomaticUpdateAttempts _updateAttempts = new();
    private readonly ClientUpdatePolicy _updatePolicy = new();

    private async Task CheckClientUpdateAsync()
    {
        if (_checkingClientUpdate || _updateBusy || _savingSettings || !_serviceInstallItem.Enabled) return;
        _checkingClientUpdate = true;
        _checkUpdateItem.Enabled = false;
        _checkUpdateItem.Text = L.T("Đang kiểm tra bản mới nhất…");
        try
        {
            await SavePrinterSelectionAsync();
            if (_printerDraftDirty) throw new InvalidOperationException(L.T("Đang lưu máy in, hãy thử lại."));
            var server = _snapshot?.Settings.ServerUrl ?? (_settingsLoaded ? _serverUrl : new ConfigService().LoadOrCreate().ServerUrl);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var updater = new ClientUpdater();
            var release = await updater.CheckAsync(server, timeout.Token);
            if (release is null)
                MessageBox.Show(this, L.T("Không có bản cập nhật client mới hơn trên server."),
                    L.T("Cập nhật ứng dụng"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            else await ApplyServerUpdateAsync(new ClientUpdateRequest(server, release));
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) MessageBox.Show(this, L.T("Đã hủy hoặc hết thời gian cập nhật."), L.T("Cập nhật ứng dụng"));
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex); }
        finally
        {
            _checkingClientUpdate = false;
            if (!IsDisposed) { _checkUpdateItem.Text = L.T("Cập nhật client…"); _checkUpdateItem.Enabled = true; }
        }
    }

    private async Task ApplyServerUpdateAsync(ClientUpdateRequest? requested = null)
    {
        var manual = requested is not null;
        if ((!manual && (_headless || !_settingsLoaded || _checkingClientUpdate)) || _setupPromptOpen || _updateBusy || _savingSettings ||
            _printerDraftDirty || !_serviceInstallItem.Enabled) return;
        var command = requested ?? _snapshot?.Update;
        var configuredServer = _snapshot?.Settings.ServerUrl ?? (_settingsLoaded ? _serverUrl : new ConfigService().LoadOrCreate().ServerUrl);
        if (command is null || command.ServerUrl != configuredServer ||
            !ClientUpdateProtocol.IsApplicableRelease(command.Release)) return;
        var administrator = SetupPaths.IsAdministrator;
        var action = manual ? ClientUpdateAction.Notify : _updatePolicy.Decide(command, administrator, Visible && WindowState != FormWindowState.Minimized);
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
            // Showing an owned dialog can also reveal its hidden owner. Keep automatic
            // updates entirely in the background when the manager is not open.
            using var progressForm = Visible && WindowState != FormWindowState.Minimized ? new UpdateProgressForm() : null;
            if (progressForm is not null)
            {
                progressForm.CancelRequested += (_, _) => _updateCancellation?.Cancel();
                progressForm.Show(this);
            }
            Enabled = false;
            SetServiceItems(false);
            try
            {
                await updater.DownloadAsync(command.ServerUrl, release, archive,
                    progressForm is null ? null : new Progress<int>(progressForm.ReportProgress), ct);
                progressForm?.ShowVerification();
                await Task.Run(() => ClientUpdater.ExtractAndVerify(archive, package, release, ct), ct);
                ct.ThrowIfCancellationRequested();
                ClientUpdateProtocol.RecordAppliedRelease(package, release);
                ClientUpdateProtocol.RecordAppliedRelease(Path.Combine(package, "Service"), release);
                progressForm?.ShowInstallation(requiresElevation: !administrator);
                var installed = await ElevatedSetup.RunAsync(SetupOperation.InstallService, null, Path.Combine(package, "RemotePrint.Client.exe"));
                if (string.IsNullOrWhiteSpace(installed) || !File.Exists(installed)) throw new InvalidOperationException(L.T("Không xác định được ứng dụng đã cập nhật để khởi động lại."));
                var restart = new ProcessStartInfo(installed) { UseShellExecute = false };
                if (!Visible || WindowState == FormWindowState.Minimized) restart.ArgumentList.Add("--background");
                restart.ArgumentList.Add("--wait-for-exit"); restart.ArgumentList.Add(Environment.ProcessId.ToString());
                using var restarted = Process.Start(restart) ?? throw new InvalidOperationException(L.T("Không xác định được ứng dụng đã cập nhật để khởi động lại."));
                StopAgent();
                Application.Exit();
            }
            finally { Enabled = true; progressForm?.Close(); SetServiceItems(true); }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && Visible && WindowState != FormWindowState.Minimized) MessageBox.Show(this, L.T("Đã hủy hoặc hết thời gian cập nhật."), L.T("Cập nhật ứng dụng"));
        }
        catch (Exception ex)
        {
            if (!IsDisposed && Visible && WindowState != FormWindowState.Minimized) ShowError(ex);
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
