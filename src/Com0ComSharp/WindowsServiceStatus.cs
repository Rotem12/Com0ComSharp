using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Com0ComSharp;

internal sealed record WindowsServiceStatus(bool Installed, uint State = 0, uint ProcessId = 0, uint ServiceType = 0)
{
    internal const string BrokerServiceName = "Com0ComSharpBroker";
    internal bool Running => Installed && State == 4 && ProcessId != 0;

    // SCM status queries are allowed to ordinary users. Opening a LocalSystem
    // process to inspect its executable is not, even with QUERY_LIMITED_INFORMATION.
    internal static WindowsServiceStatus Query(string name = BrokerServiceName)
    {
        WindowsDiagnostics.EnsureWindows();
        var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query Windows Service Control Manager.");
        try
        {
            var service = OpenService(manager, name, 4); // SERVICE_QUERY_STATUS
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1060) return new(false);
                throw new Win32Exception(error, "Cannot query the " + name + " service.");
            }
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the " + name + " service status.");
                if (name == BrokerServiceName && !ManagementBrokerLocation.HasProtectedRegistration())
                    throw new Win32Exception(5, "The broker service executable must be registered under Program Files\\Com0ComSharp.");
                return new(true, status.CurrentState, status.ProcessId, status.ServiceType);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr service);
}
