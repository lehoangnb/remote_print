namespace RemotePrint.Client;

internal static class ClientUninstaller
{
    internal static bool Confirm(IWin32Window? owner) => MessageBox.Show(owner,
        L.T("Gỡ ứng dụng Remote Print, Windows Service, mục tự khởi động và shortcut khỏi máy này?\n\n") +
        L.T("Cấu hình ghép nối và file in đã nhận được giữ lại để có thể cài lại. Có thể cần khởi động lại Windows để xóa hết file đang sử dụng."),
        L.T("Gỡ cài đặt Remote Print"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    internal static void ShowSuccess(IWin32Window? owner) => MessageBox.Show(owner,
        L.T("Đã gỡ Remote Print. Khởi động lại Windows nếu còn file trong Program Files\\RemotePrint."),
        L.T("Gỡ cài đặt Remote Print"), MessageBoxButtons.OK, MessageBoxIcon.Information);

    internal static int RunStandalone()
    {
        if (!Confirm(null)) return 0;
        try
        {
            // This command also runs when invoked by Windows Installed Apps, before
            // the normal WinForms message loop is created.
            Task.Run(() => ElevatedSetup.RunAsync(SetupOperation.UninstallClient, migrationFile: null))
                .GetAwaiter().GetResult();
            Program.RequestClientExit();
            ShowSuccess(null);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.Message(ex.Message), L.T("Gỡ cài đặt Remote Print"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
