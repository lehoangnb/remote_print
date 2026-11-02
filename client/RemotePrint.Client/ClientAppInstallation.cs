using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace RemotePrint.Client;

internal interface IClientAppRegistration
{
    void Register(string executable);
    void Unregister();
}

// The Start Menu entries and Windows Installed Apps entry always point at the
// active version. Updates replace those targets after the service has started.
internal sealed class ClientAppRegistration : IClientAppRegistration
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RemotePrint";
    private static string MenuDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Remote Print");

    public void Register(string executable)
    {
        var full = Path.GetFullPath(executable);
        var versions = Path.GetFullPath(Path.Combine(SetupPaths.InstallRoot, "versions")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(versions, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(full), "RemotePrint.Client.exe", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(full)), "N", out _) || !File.Exists(full))
            throw new InvalidOperationException("Đường dẫn client đã cài không hợp lệ.");

        Directory.CreateDirectory(MenuDirectory);
        CreateShortcut(Path.Combine(MenuDirectory, "Remote Print.lnk"), full, "", "Mở Remote Print");
        CreateShortcut(Path.Combine(MenuDirectory, "Gỡ cài đặt Remote Print.lnk"), full,
            "--uninstall", "Gỡ cài đặt Remote Print");
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey, true)
            ?? throw new InvalidOperationException("Không đăng ký được ứng dụng trong Windows Settings.");
        key.SetValue("DisplayName", "Remote Print");
        key.SetValue("DisplayVersion", ClientUpdater.CurrentVersion.ToString());
        key.SetValue("Publisher", "Remote Print");
        key.SetValue("InstallLocation", SetupPaths.InstallRoot);
        key.SetValue("DisplayIcon", full + ",0");
        key.SetValue("UninstallString", $"\"{full}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public void Unregister()
    {
        Registry.LocalMachine.DeleteSubKey(UninstallKey, throwOnMissingSubKey: false);
        foreach (var name in new[] { "Remote Print.lnk", "Gỡ cài đặt Remote Print.lnk" })
        {
            var path = Path.Combine(MenuDirectory, name);
            if (File.Exists(path)) File.Delete(path);
        }
        if (Directory.Exists(MenuDirectory) && !Directory.EnumerateFileSystemEntries(MenuDirectory).Any())
            Directory.Delete(MenuDirectory);
    }

    internal static void CreateShortcut(string link, string executable, string arguments, string description)
    {
        // WScript.Shell.Save converts the link filename through the system ANSI
        // code page on some Windows installations. Persist the native Unicode link.
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
            ?? throw new InvalidOperationException("Windows Shell không hỗ trợ tạo shortcut.");
        object shell = Activator.CreateInstance(type)!;
        try
        {
            var shortcut = (IShellLinkW)shell;
            shortcut.SetPath(executable);
            shortcut.SetArguments(arguments);
            shortcut.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            shortcut.SetIconLocation(executable, 0);
            shortcut.SetDescription(description);
            ((IPersistFile)shell).Save(link, true);
        }
        finally
        {
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}

internal interface IClientInstallCleanup
{
    void RemoveVersions(string installRoot);
}

// Windows keeps the executing apphost/DLLs open. Delete everything that is free
// now and schedule only locked files and their directories for the next reboot.
internal sealed class ClientInstallCleanup(string? allowedRoot = null) : IClientInstallCleanup
{
    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string? replacement, int flags);

    public void RemoveVersions(string installRoot)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(root, Path.GetFullPath(allowedRoot ?? SetupPaths.InstallRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Thư mục cài đặt không hợp lệ.");
        if (Directory.Exists(root)) EnsureOrdinaryDirectory(root);
        var versions = Path.Combine(root, "versions");
        if (!Directory.Exists(versions)) return;
        EnsureOrdinaryDirectory(versions);
        foreach (var directory in Directory.EnumerateDirectories(versions))
        {
            // Only version folders created by the installer may be removed.
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            RemoveDirectory(directory);
        }
        // A running client can keep its version directory open until reboot.
        // Schedule parent folders only if they contain installer-owned versions.
        var remaining = Directory.EnumerateFileSystemEntries(versions).ToArray();
        var versionsScheduledForRemoval = false;
        if (remaining.Length == 0 || remaining.All(path => Directory.Exists(path) &&
                Guid.TryParseExact(Path.GetFileName(path), "N", out _)))
        {
            DeleteOrDeferDirectory(versions);
            versionsScheduledForRemoval = true;
        }
        if (Directory.Exists(root))
        {
            var rootEntries = Directory.EnumerateFileSystemEntries(root).ToArray();
            if (rootEntries.Length == 0 || (rootEntries.Length == 1 &&
                versionsScheduledForRemoval && string.Equals(rootEntries[0], versions, StringComparison.OrdinalIgnoreCase)))
                DeleteOrDeferDirectory(root);
        }
    }

    private static void RemoveDirectory(string directory)
    {
        EnsureOrdinaryDirectory(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Phát hiện liên kết thư mục/file trong bản cài đặt; dừng gỡ để bảo vệ dữ liệu.");
            if ((attributes & FileAttributes.Directory) != 0) RemoveDirectory(entry);
            else
            {
                try { File.Delete(entry); }
                catch (IOException) { Defer(entry); }
                catch (UnauthorizedAccessException) { Defer(entry); }
            }
        }
        DeleteOrDeferDirectory(directory);
    }

    private static void EnsureOrdinaryDirectory(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Thư mục cài đặt là liên kết; dừng gỡ để bảo vệ dữ liệu.");
    }

    private static void DeleteOrDeferDirectory(string directory)
    {
        try { Directory.Delete(directory); }
        catch (IOException) { Defer(directory); }
        catch (UnauthorizedAccessException) { Defer(directory); }
    }

    private static void Defer(string path)
    {
        if (!MoveFileEx(path, null, MoveFileDelayUntilReboot))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không xóa hoặc đặt lịch xóa được: " + path);
    }
}
