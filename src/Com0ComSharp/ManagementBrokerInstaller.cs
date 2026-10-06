using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Com0ComSharp;

internal static class ManagementBrokerInstaller
{
    private const string ServiceName = "Com0ComSharpBroker";
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 2;
    private const uint ServiceErrorNormal = 1;

    internal static bool IsAdministrator() => WindowsDiagnostics.IsAdministrator();

    internal static void Install(DriverPackage package, string brokerSource, string expectedBrokerHash)
    {
        WindowsDiagnostics.EnsureWindows();
        if (!IsAdministrator()) throw new UnauthorizedAccessException("Administrator approval is required to install the management service.");
        VerifyBrokerForInstall(brokerSource, expectedBrokerHash);
        StopAndDeleteService();

        var root = GetInstallRoot();
        var driverDirectory = Path.Combine(root, "Driver");
        Directory.CreateDirectory(driverDirectory);
        foreach (var name in DriverPackage.RequiredFiles)
            File.Copy(Path.Combine(package.DirectoryPath, name), Path.Combine(driverDirectory, name), true);
        var serviceExecutable = Path.Combine(root, "Com0ComSharp.Broker.exe");
        File.Copy(brokerSource, serviceExecutable, true);
        VerifyFileHash(serviceExecutable, expectedBrokerHash);

        var protectedPackage = DriverPackage.Open(driverDirectory);
        if (!SameFingerprint(package.Fingerprint(), protectedPackage.Fingerprint()))
            throw new InvalidDataException("The protected driver package differs from the package used during setup.");

        var commandLine = "\"" + serviceExecutable + "\" --service \"" + driverDirectory + "\"";
        var manager = OpenSCManager(null, null, ScManagerAllAccess);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open Windows Service Control Manager.");
        try
        {
            var service = CreateService(manager, ServiceName, "Com0ComSharp Pair Management Broker", ServiceAllAccess,
                ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal, commandLine, null, IntPtr.Zero,
                null, "LocalSystem", null);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the Com0ComSharp management service.");
            try
            {
                if (!StartService(service, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the Com0ComSharp management service.");
                WaitForState(service, 4);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static void VerifyBrokerForInstall(string path, string expectedHash)
    {
        if (!Path.GetFileName(path).Equals("Com0ComSharp.Broker.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The service executable must be named Com0ComSharp.Broker.exe.");
        VerifyFileHash(path, expectedHash);
    }

    internal static void StopForDriverRemoval()
    {
        using var service = OpenInstalledService();
        if (service is null) return;
        StopService(service.Handle);
    }

    internal static void RestoreAfterFailedDriverRemoval()
    {
        using var service = OpenInstalledService();
        if (service is null) return;
        if (!StartService(service.Handle, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restart the management service after driver removal failed.");
        WaitForState(service.Handle, 4);
    }

    internal static void Uninstall()
    {
        WindowsDiagnostics.EnsureWindows();
        if (!IsAdministrator()) throw new UnauthorizedAccessException("Administrator approval is required to remove the management service.");
        StopAndDeleteService();
        var root = GetInstallRoot();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return RuntimeCompatibility.ToHexString(sha.ComputeHash(stream));
    }

    private static void VerifyFileHash(string path, string expected)
    {
        if (!File.Exists(path) || !string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The broker executable changed before elevation completed.");
    }

    private static bool SameFingerprint(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(x => right.TryGetValue(x.Key, out var hash) && string.Equals(hash, x.Value, StringComparison.OrdinalIgnoreCase));

    private static string GetInstallRoot()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, "Com0ComSharp");
    }

    private static void StopAndDeleteService()
    {
        var manager = OpenSCManager(null, null, ScManagerAllAccess);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, ServiceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == 1060) return;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the existing management service.");
            }
            try
            {
                StopService(service);
                if (!DeleteService(service) && Marshal.GetLastWin32Error() != 1072)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the existing management service registration.");
            }
            finally { CloseServiceHandle(service); }
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var remaining = OpenService(manager, ServiceName, ServiceAllAccess);
                if (remaining == IntPtr.Zero && Marshal.GetLastWin32Error() == 1060) return;
                if (remaining != IntPtr.Zero) CloseServiceHandle(remaining);
                Thread.Sleep(250);
            }
            throw new TimeoutException("The old management service is still being removed.");
        }
        finally { CloseServiceHandle(manager); }
    }

    private static void StopService(IntPtr service)
    {
        if (!ControlService(service, 1, out var status))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1062 && error != 1061) throw new Win32Exception(error, "Could not stop the management service.");
            if (error == 1062) return;
        }
        WaitForState(service, 1);
    }

    private static void WaitForState(IntPtr service, uint desired)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (status.CurrentState == desired) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException("The management service did not reach the requested state.");
    }

    private sealed class ServiceHandle(IntPtr handle) : IDisposable
    {
        internal IntPtr Handle { get; } = handle;
        public void Dispose() { if (Handle != IntPtr.Zero) CloseServiceHandle(Handle); }
    }

    private static ServiceHandle? OpenInstalledService()
    {
        var manager = OpenSCManager(null, null, ScManagerAllAccess);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, ServiceName, ServiceAllAccess);
            if (service == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == 1060) return null;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return new(service);
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateService(IntPtr manager, string name, string displayName, uint desiredAccess, uint serviceType, uint startType, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(IntPtr service, uint argCount, IntPtr args);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
