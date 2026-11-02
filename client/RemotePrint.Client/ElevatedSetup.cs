using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace RemotePrint.Client;

public enum SetupOperation
{
    InstallService,
    UninstallService,
    UninstallClient,
    EnableAdminStartup,
    DisableAdminStartup
}

/// <summary>
/// Runs the installers in an elevated copy of this same executable instead of an
/// elevated PowerShell. The child reports back through a result file because a
/// shell-executed process cannot pipe its output to the caller.
/// </summary>
internal static class ElevatedSetup
{
    private const string SetupSwitch = "--setup";
    private const string ResultSwitch = "--result";
    private const string UserSwitch = "--user";
    private const string MigrateSwitch = "--migrate";
    private const string ResultPrefix = "remoteprint-setup-";
    private const int ErrorCanceledByUser = 1223;

    // UAC may launch the child as a different administrator account. That account
    // has a different %TEMP%, so both processes use a machine-wide result folder.
    private static string ResultDirectory => Path.Combine(SetupPaths.DataRoot, "SetupResults");

    public static bool IsRequested(string[] args) => args.Contains(SetupSwitch);

    internal static ProcessStartInfo StartInfo(string executable, bool administrator) => administrator
        ? new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };

    public static async Task<string?> RunAsync(SetupOperation operation, string? migrationFile, string? packageExecutable = null, bool restartClient = false)
    {
        var executable = packageExecutable ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Không xác định được tệp thực thi để nâng quyền.");
        var result = Path.Combine(ResultDirectory, $"{ResultPrefix}{Guid.NewGuid():N}.json");
        var start = StartInfo(executable, SetupPaths.IsAdministrator);
        start.ArgumentList.Add(SetupSwitch);
        start.ArgumentList.Add(operation.ToString());
        start.ArgumentList.Add(ResultSwitch);
        start.ArgumentList.Add(result);
        start.ArgumentList.Add(UserSwitch);
        start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        if (restartClient)
        {
            start.ArgumentList.Add("--restart-after");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
        }
        if (migrationFile is not null)
        {
            start.ArgumentList.Add(MigrateSwitch);
            start.ArgumentList.Add(migrationFile);
        }
        try
        {
            int exitCode;
            try
            {
                using var process = Process.Start(start)
                    ?? throw new InvalidOperationException("Không khởi động được trình cài đặt.");
                await process.WaitForExitAsync();
                exitCode = process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCanceledByUser)
            {
                throw new InvalidOperationException("Bạn đã hủy yêu cầu quyền quản trị nên dịch vụ chưa thay đổi.");
            }
            if (!File.Exists(result))
                throw new InvalidOperationException(
                    $"Trình cài đặt đã thoát (mã {exitCode}) nhưng không trả kết quả. Hãy kiểm tra Runtime và quyền quản trị.");
            var failure = ReadFailure(result);
            if (failure is not null) throw new InvalidOperationException(failure);
            if (exitCode != 0)
                throw new InvalidOperationException($"Trình cài đặt thất bại (mã {exitCode}).");
            using var document = JsonDocument.Parse(File.ReadAllText(result));
            return document.RootElement.TryGetProperty("installedPath", out var installed) && installed.ValueKind == JsonValueKind.String ? installed.GetString() : null;
        }
        finally { TryDeleteResult(result); }
    }

    // Entry point for the elevated child: never throws, because there is no console to
    // carry a stack trace and the parent only reads the result file.
    public static int RunChild(string[] args)
    {
        var index = Array.IndexOf(args, SetupSwitch);
        if (index < 0 || index + 1 >= args.Length ||
            !Enum.TryParse<SetupOperation>(args[index + 1], out var operation) || !Enum.IsDefined(operation))
            return 2;
        var result = Value(args, ResultSwitch);
        if (result is null || !IsSafeResultPath(result)) return 3;
        string? failure = null;
        string? installedPath = null;
        try
        {
            PrepareResultDirectory();
            installedPath = Execute(operation, Value(args, UserSwitch), Value(args, MigrateSwitch));
            if (operation == SetupOperation.EnableAdminStartup && installedPath is not null &&
                int.TryParse(Value(args, "--restart-after"), out var previousId) && previousId > 0)
            {
                var restart = new ProcessStartInfo(installedPath) { UseShellExecute = false };
                restart.ArgumentList.Add("--wait-for-exit"); restart.ArgumentList.Add(previousId.ToString());
                using var child = Process.Start(restart) ?? throw new InvalidOperationException("Không khởi động được client với quyền quản trị.");
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        try
        {
            using (var file = new FileStream(result, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, new { ok = failure is null, message = failure, installedPath });
            ProtectResultFile(result, TrustedSid(Value(args, UserSwitch)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TryDeleteResult(result);
            return 4;
        }
        return failure is null ? 0 : 1;
    }

    private static string? Execute(SetupOperation operation, string? userSid, string? migrationFile)
    {
        switch (operation)
        {
            case SetupOperation.InstallService:
                SetupPaths.RequireAdministrator("cập nhật Remote Print");
                PrinterService.RemoveObsoleteVirtualQueue();
                return new WindowsServiceInstaller(new ServiceRegistrar())
                    .Install(TrustedSid(userSid), TrustedMigration(migrationFile));
            case SetupOperation.UninstallService:
                new WindowsServiceInstaller(new ServiceRegistrar()).Uninstall(TrustedSid(userSid));
                break;
            case SetupOperation.UninstallClient:
                new WindowsServiceInstaller(new ServiceRegistrar()).UninstallClient(TrustedSid(userSid));
                break;
            case SetupOperation.EnableAdminStartup:
            case SetupOperation.DisableAdminStartup:
                if (operation == SetupOperation.EnableAdminStartup && userSid != AdminAgentStartup.CurrentSid)
                    throw new InvalidOperationException("Chạy admin khi đăng nhập cần tài khoản Windows hiện tại thuộc nhóm Administrators. Không thể dùng quyền của tài khoản khác cho tùy chọn này.");
                return AdminAgentStartup.Configure(TrustedSid(userSid), operation == SetupOperation.EnableAdminStartup);
        }
        return null;
    }

    // The caller passes its own SID so a service installed under another admin's consent
    // still authorizes the user who clicked. A SID that is not a signed-in user account is
    // ignored in favour of the elevated process' own identity.
    private static string TrustedSid(string? candidate)
    {
        var own = WindowsIdentity.GetCurrent().User!.Value;
        if (string.IsNullOrWhiteSpace(candidate) || candidate == own) return own;
        try
        {
            // A loaded HKEY_USERS hive is the proof that the SID belongs to a signed-in
            // profile, which is what the Run value and access.json are written for; the
            // constructor has already rejected anything that is not a well-formed SID.
            _ = new SecurityIdentifier(candidate);
            return Registry.Users.OpenSubKey(candidate) is not null ? candidate : own;
        }
        catch (ArgumentException) { return own; }
    }

    // ConfigService writes the migration next to the user's own config; anything else is a
    // path the elevated process should not be reading.
    private static string? TrustedMigration(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemotePrint"));
        var full = Path.GetFullPath(candidate);
        var name = Path.GetFileName(full);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
               name.StartsWith("service-migration-", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(full)
            ? full
            : null;
    }

    private static bool IsSafeResultPath(string candidate)
    {
        try
        {
            var full = Path.GetFullPath(candidate);
            var filename = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), ResultDirectory, StringComparison.OrdinalIgnoreCase) ||
                !filename.StartsWith(ResultPrefix, StringComparison.OrdinalIgnoreCase) ||
                !filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return false;
            var identifier = filename[ResultPrefix.Length..^".json".Length];
            return Guid.TryParseExact(identifier, "N", out _);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void PrepareResultDirectory()
    {
        Directory.CreateDirectory(SetupPaths.DataRoot);
        if ((File.GetAttributes(SetupPaths.DataRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Thư mục dữ liệu Remote Print không được là junction hoặc symbolic link.");
        Directory.CreateDirectory(ResultDirectory);
        if ((File.GetAttributes(ResultDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Thư mục kết quả cài đặt không được là junction hoặc symbolic link.");

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(ResultDirectory).SetAccessControl(acl);
    }

    private static void ProtectResultFile(string result, string callerSid)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
        // The requesting user may read and delete the result, but cannot alter it.
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(callerSid),
            FileSystemRights.Read | FileSystemRights.Delete, AccessControlType.Allow));
        new FileInfo(result).SetAccessControl(acl);
    }

    private static void TryDeleteResult(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? ReadFailure(string path)
    {
        try
        {
            if (!IsSafeResultPath(path) || !File.Exists(path))
                return "Không tìm thấy tệp kết quả cài đặt hợp lệ.";
            return ParseResult(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return $"Không đọc được kết quả trình cài đặt: {ex.Message}";
        }
    }

    internal static string? ParseResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return "Kết quả trình cài đặt thiếu trạng thái thành công/thất bại hợp lệ.";
            if (ok.GetBoolean()) return null;
            return root.TryGetProperty("message", out var message) &&
                   message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString())
                ? message.GetString()
                : "Trình cài đặt báo lỗi nhưng không nêu nguyên nhân.";
        }
        catch (JsonException)
        {
            return "Tệp kết quả trình cài đặt chứa JSON không hợp lệ.";
        }
    }

    private static string? Value(string[] args, string switchName)
    {
        var index = Array.IndexOf(args, switchName);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

}
