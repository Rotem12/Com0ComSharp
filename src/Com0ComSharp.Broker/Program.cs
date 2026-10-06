using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Com0ComSharp;

if (args.Length == 2 && args[0] == "--service")
{
    BrokerService.Run(Path.GetFullPath(args[1]));
    return;
}
Console.Error.WriteLine("This executable is installed and run by Windows Service Control Manager.");
return;

internal static class BrokerService
{
    private const string ServiceName = "Com0ComSharpBroker";
    private const string PipeName = "Com0ComSharp.Management.v1";
    private static readonly CancellationTokenSource Stop = new();
    private static string packagePath = "";
    private static ServiceMainCallback? serviceMain;
    private static HandlerCallback? handler;
    private static IntPtr statusHandle;

    internal static void Run(string directory)
    {
        packagePath = directory;
        serviceMain = OnServiceMain;
        var table = new[] { new ServiceTableEntry { Name = ServiceName, Main = serviceMain }, new ServiceTableEntry() };
        if (!StartServiceCtrlDispatcher(table)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void OnServiceMain(uint argc, IntPtr argv)
    {
        handler = OnControl;
        statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, handler, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero) return;
        SetStatus(2, 0, 30000);
        try
        {
            var package = DriverPackage.Open(packagePath);
            ServeAsync(package, Stop.Token).GetAwaiter().GetResult();
            SetStatus(1, 0, 0);
        }
        catch
        {
            SetStatus(1, 1, 0);
        }
    }

    private static uint OnControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control == 1) { SetStatus(3, 0, 30000); Stop.Cancel(); }
        return 0;
    }

    private static void SetStatus(uint state, uint error, uint waitHint)
    {
        if (statusHandle == IntPtr.Zero) return;
        var status = new ServiceStatus { ServiceType = 0x10, CurrentState = state, ControlsAccepted = state == 4 ? 1u : 0u, Win32ExitCode = error, WaitHint = waitHint };
        SetServiceStatus(statusHandle, ref status);
    }

    private static async Task ServeAsync(DriverPackage package, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = CreatePipe();
            SetStatus(4, 0, 0); // Running means the pipe is listening, not just that the process exists.
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                var line = await RuntimeCompatibility.ReadBoundedLineAsync(reader, 65536, requestDeadline.Token).ConfigureAwait(false);
                var result = line is null
                    ? new CommandResult(Com0ComOperation.RemovePair, -1, "Empty request.", FailureKind.HelperFailed)
                    : await ManagementBrokerProtocol.ProcessAsync(package, line,
                        (command, allowLegacy, token) => new Com0ComClient(package,
                            new ClientOptions(elevation: ElevationMode.RequireAdministrator, allowLegacyDriver: allowLegacy)).ExecuteAsync(command, token),
                        WindowsDiagnostics.GetPairs, () => StagedDriverPackage.IsInstalled(package), stop).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                if (pipe.IsConnected)
                {
                    try
                    {
                        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new CommandResult(Com0ComOperation.Help, -1, e.Message, FailureKind.HelperFailed))).ConfigureAwait(false);
                    }
                    catch (IOException) { } // A disconnected caller must not terminate the service.
                }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 65536, 65536, security);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ServiceTableEntry { public string? Name; public ServiceMainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainCallback(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint HandlerCallback(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerCallback callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
