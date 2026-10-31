using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RemotePrint.Client;

// .NET's ServiceController only starts, stops and reads a service; registering one
// with a description, delayed auto start and a restart policy is service control
// manager work, so the installer speaks advapi32 directly.
internal static class NativeWindowsService
{
    public const int Stopped = 1;
    public const int Running = 4;

    public const uint AccessQueryConfig = 0x0001;
    public const uint AccessChangeConfig = 0x0002;
    public const uint AccessQueryStatus = 0x0004;
    public const uint AccessStart = 0x0010;
    public const uint AccessStop = 0x0020;
    public const uint AccessDelete = 0x00010000;
    public const uint ManagerConnect = 0x0001;
    public const uint ManagerCreateService = 0x0002;

    public const uint OwnProcess = 0x00000010;
    public const uint AutoStart = 0x00000002;
    public const uint ErrorNormal = 0x00000001;
    public const uint NoChange = 0xFFFFFFFF;

    public const uint ConfigDescription = 1;
    public const uint ConfigFailureActions = 2;
    public const uint ConfigDelayedAutoStart = 3;
    public const uint ConfigFailureActionsFlag = 4;

    private const int ControlStop = 1;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotRunning = 1062;

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Action
    {
        public uint Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DelayedAutoStart
    {
        public int IsDelayedAutoStart;
    }

    // SCM expects a single MULTI_SZ, not an array of pointers.
    private sealed class DependencyList : IDisposable
    {
        public IntPtr Pointer { get; }

        public DependencyList(string[] names)
        {
            if (names.Length == 0)
            {
                Pointer = IntPtr.Zero;
                return;
            }
            if (names.Any(name => string.IsNullOrEmpty(name) || name.Contains('\0')))
                throw new ArgumentException("Tên dịch vụ phụ thuộc không hợp lệ.", nameof(names));
            Pointer = Marshal.StringToHGlobalUni(string.Join('\0', names) + "\0");
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero) Marshal.FreeHGlobal(Pointer);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateServiceW(IntPtr manager, string name, string displayName, uint access,
        uint type, uint startType, uint errorControl, string binaryPath, string? group, IntPtr tag,
        IntPtr dependencies, string? startName, IntPtr password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint type, uint startType, uint errorControl,
        string? binaryPath, string? group, IntPtr tag, IntPtr dependencies, string? startName, IntPtr password,
        string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint infoClass, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceW(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, int control, ref Status status);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool QueryServiceStatus(IntPtr service, ref Status status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    public static IntPtr OpenManager(uint access)
    {
        var handle = OpenSCManager(null, null, access);
        if (handle == IntPtr.Zero) throw LastError("Không mở được Service Control Manager.");
        return handle;
    }

    public static IntPtr Create(IntPtr manager, string name, string displayName, string binaryPath,
        string[] dependencies)
    {
        using var list = new DependencyList(dependencies);
        var handle = CreateServiceW(manager, name, displayName,
            AccessQueryConfig | AccessChangeConfig | AccessQueryStatus | AccessStart | AccessStop,
            OwnProcess, AutoStart, ErrorNormal, binaryPath, null, IntPtr.Zero, list.Pointer, null, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw LastError($"Không tạo được dịch vụ {name}.");
        return handle;
    }

    public static IntPtr TryOpen(IntPtr manager, string name, uint access) =>
        OpenServiceW(manager, name, access);

    public static IntPtr Open(IntPtr manager, string name, uint access)
    {
        var handle = TryOpen(manager, name, access);
        if (handle == IntPtr.Zero) throw LastError($"Không mở được dịch vụ {name}.");
        return handle;
    }

    public static void UpdateBinaryPath(IntPtr service, string binaryPath)
    {
        if (!ChangeServiceConfigW(service, NoChange, NoChange, NoChange, binaryPath, null, IntPtr.Zero,
                IntPtr.Zero, null, IntPtr.Zero, null))
            throw LastError("Không cập nhật được đường dẫn tệp của dịch vụ.");
    }

    public static void SetDescription(IntPtr service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description);
        var info = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(info, text); // SERVICE_DESCRIPTIONW.lpDescription
            SetInfo(service, ConfigDescription, info, "mô tả");
        }
        finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(text); }
    }

    public static void SetDelayedAutoStart(IntPtr service, bool delayed)
    {
        var info = new DelayedAutoStart { IsDelayedAutoStart = delayed ? 1 : 0 };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<DelayedAutoStart>());
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            SetInfo(service, ConfigDelayedAutoStart, pointer, "chế độ khởi động chậm");
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    // Restart at 5 s, 15 s and 60 s with a 24 h reset period, and apply the actions from
    // the first failure instead of only after the second one.
    public static void SetRestartPolicy(IntPtr service, uint[] delayMilliseconds)
    {
        var actions = Marshal.AllocHGlobal(Marshal.SizeOf<Action>() * delayMilliseconds.Length);
        try
        {
            for (var index = 0; index < delayMilliseconds.Length; index++)
            {
                var action = new Action { Type = 1, Delay = delayMilliseconds[index] };
                Marshal.StructureToPtr(action, IntPtr.Add(actions, Marshal.SizeOf<Action>() * index), false);
            }
            var failure = new FailureActions
            {
                ResetPeriod = 86400, RebootMessage = IntPtr.Zero, Command = IntPtr.Zero,
                Actions = actions, ActionCount = (uint)delayMilliseconds.Length
            };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FailureActions>());
            try
            {
                Marshal.StructureToPtr(failure, pointer, false);
                SetInfo(service, ConfigFailureActions, pointer, "chính sách khởi động lại");
                var flag = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(flag, 1);
                    SetInfo(service, ConfigFailureActionsFlag, flag, "cờ bật chính sách khởi động lại");
                }
                finally { Marshal.FreeHGlobal(flag); }
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        finally { Marshal.FreeHGlobal(actions); }
    }

    public static void Start(IntPtr service)
    {
        if (!StartServiceW(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            // An instance already running is the state the caller wanted.
            if (error != ErrorServiceAlreadyRunning)
                throw new Win32Exception(error, "Không khởi động được dịch vụ Remote Print.");
        }
    }

    public static void Stop(IntPtr service)
    {
        var status = default(Status);
        if (!ControlService(service, ControlStop, ref status))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceNotRunning)
                throw new Win32Exception(error, "Không dừng được dịch vụ Remote Print.");
        }
    }

    public static void Delete(IntPtr service)
    {
        if (!DeleteService(service))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Không xoá được dịch vụ Remote Print (lỗi Windows {error}: {new Win32Exception(error).Message}).");
        }
    }

    public static int State(IntPtr service)
    {
        var status = default(Status);
        if (!QueryServiceStatus(service, ref status)) throw LastError("Không đọc được trạng thái dịch vụ.");
        return status.CurrentState;
    }

    public static void WaitForState(IntPtr service, int wanted, TimeSpan timeout, string label)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = State(service);
            if (current == wanted) return;
            if (current == Stopped && wanted == Running)
                throw new InvalidOperationException(
                    "Dịch vụ dừng ngay sau khi được khởi động. Xem log trong ProgramData\\RemotePrint.");
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Dịch vụ chưa chuyển sang trạng thái {label} sau {timeout.TotalSeconds:0} giây.");
    }

    private static void SetInfo(IntPtr service, uint infoClass, IntPtr info, string label)
    {
        if (!ChangeServiceConfig2W(service, infoClass, info))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Không đặt được {label} cho dịch vụ (lỗi Windows {error}: {new Win32Exception(error).Message}).");
        }
    }

    private static Win32Exception LastError(string message) => new(Marshal.GetLastWin32Error(), message);
}
