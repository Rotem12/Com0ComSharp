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

internal sealed record ManagementBrokerRequest(Dictionary<string, string> PackageHashes, Com0ComCommand? Command, bool StopAll, bool AllowLegacy, bool Ping = false);

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
            SetStatus(4, 0, 0);
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
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                var line = await ReadBoundedLineAsync(pipe, stop).ConfigureAwait(false);
                var result = line is null
                    ? new CommandResult(Com0ComOperation.RemovePair, -1, "Empty request.", FailureKind.HelperFailed)
                    : await ProcessAsync(package, line, stop).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                if (pipe.IsConnected)
                {
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new CommandResult(Com0ComOperation.RemovePair, -1, e.Message, FailureKind.HelperFailed))).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<CommandResult> ProcessAsync(DriverPackage package, string json, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<ManagementBrokerRequest>(json) ?? throw new InvalidDataException("Empty request.");
        var actual = package.Fingerprint();
        if (request.PackageHashes.Count != actual.Count || actual.Any(x => !request.PackageHashes.TryGetValue(x.Key, out var hash) || !string.Equals(hash, x.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The caller's driver package does not match the service's protected package.");
        if (request.Ping)
            return WindowsDiagnostics.IsDriverServiceInstalled()
                ? new(Com0ComOperation.Help, 0, "The com0com driver and management service are available.", FailureKind.None)
                : new(Com0ComOperation.Help, 2, "The com0com driver service is not registered.", FailureKind.ElevationRequired);
        var client = new Com0ComClient(package, new ClientOptions(elevation: ElevationMode.RequireAdministrator, allowLegacyDriver: request.AllowLegacy));
        if (request.StopAll)
        {
            foreach (var pair in WindowsDiagnostics.GetPairs())
            {
                var removed = await client.DestroyPairAsync(pair.Index, cancellationToken).ConfigureAwait(false);
                if (!removed.Success) return removed;
            }
            return new(Com0ComOperation.RemovePair, 0, "All com0com pairs were removed; the driver remains installed.", FailureKind.None);
        }
        var command = request.Command ?? throw new InvalidDataException("A management command is required.");
        if (command.Operation is not (Com0ComOperation.CreateNamedPair or Com0ComOperation.CreateNamedConnector or Com0ComOperation.RemovePair or Com0ComOperation.ChangePort))
            throw new InvalidDataException("The management service accepts only pair creation, removal, and port configuration.");
        _ = command.ToArguments();
        return await client.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string?> ReadBoundedLineAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length <= 65536)
        {
            var count = await stream.ReadAsync(one.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') buffer.WriteByte(one[0]);
        }
        if (buffer.Length > 65536) throw new InvalidDataException("Request exceeds the service limit.");
        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ServiceTableEntry { public string? Name; public ServiceMainCallback? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainCallback(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint HandlerCallback(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerCallback callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
