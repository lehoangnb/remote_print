using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePrint.Client;

internal enum ServiceInstallationState { Installed, Missing, Unknown }

// A failed named-pipe request says nothing about whether Windows still has the
// service registered. During upgrades the service is deliberately stopped.
internal static class ServiceInstallationProbe
{
    private const int ErrorServiceDoesNotExist = 1060;

    internal static ServiceInstallationState FromOpenResult(bool opened, int error) =>
        opened ? ServiceInstallationState.Installed :
        error == ErrorServiceDoesNotExist ? ServiceInstallationState.Missing :
        ServiceInstallationState.Unknown;

    internal static ServiceInstallationState Check()
    {
        try
        {
            var manager = NativeWindowsService.OpenManager(NativeWindowsService.ManagerConnect);
            try
            {
                // QUERY_STATUS is sufficient for both standard and elevated clients.
                // Installer privileges (CHANGE_CONFIG/START) are not required here.
                var service = NativeWindowsService.TryOpen(manager, SetupPaths.ServiceName,
                    NativeWindowsService.AccessQueryStatus);
                var error = service == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                if (service != IntPtr.Zero) NativeWindowsService.CloseServiceHandle(service);
                return FromOpenResult(service != IntPtr.Zero, error);
            }
            finally { NativeWindowsService.CloseServiceHandle(manager); }
        }
        catch (Win32Exception)
        {
            // SCM could not be queried: do not mistake that for an uninstalled service.
            return ServiceInstallationState.Unknown;
        }
    }
}
