namespace RemotePrint.Client;

public sealed partial class MainForm
{
    private async Task ChangeAdminStartupAsync()
    {
        if (_updateBusy || _savingSettings || !_serviceInstallItem.Enabled) return;
        var enabled = !_adminStartupItem.Checked;
        _adminStartupItem.Enabled = false;
        SetServiceItems(false);
        try
        {
            var restart = enabled && !SetupPaths.IsAdministrator;
            await ElevatedSetup.RunAsync(enabled ? SetupOperation.EnableAdminStartup : SetupOperation.DisableAdminStartup,
                null, restartClient: restart);
            _adminStartupItem.Checked = AdminAgentStartup.IsEnabled(AdminAgentStartup.CurrentSid);
            if (restart) { StopAgent(); Application.Exit(); }
            else if (!enabled && SetupPaths.IsAdministrator)
                MessageBox.Show(this, L.T("Đã tắt chế độ admin cho lần mở client và đăng nhập Windows tiếp theo. Phiên hiện tại vẫn có quyền quản trị."),
                    "Remote Print", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _adminStartupItem.Enabled = true; SetServiceItems(true); }
    }
}
