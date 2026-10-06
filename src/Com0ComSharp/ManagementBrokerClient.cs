using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Com0ComSharp;

internal interface IManagementBroker
{
    Task<CommandResult> ProbeAsync(CancellationToken cancellationToken);
    Task<CommandResult> ExecuteAsync(Com0ComCommand command, CancellationToken cancellationToken);
    Task<CommandResult> StopAllAsync(CancellationToken cancellationToken);
}

/// <summary>Authenticated transport to the installed management service; never requests elevation.</summary>
internal sealed class ManagementBrokerClient : IManagementBroker
{
    internal const string PipeName = "Com0ComSharp.Management.v1";
    private readonly DriverPackage package;
    private readonly ClientOptions options;
    private readonly string pipeName;
    private readonly Func<WindowsServiceStatus> queryService;

    internal ManagementBrokerClient(DriverPackage package, ClientOptions options, string pipeName = PipeName, Func<WindowsServiceStatus>? queryService = null)
    {
        this.package = package;
        this.options = options;
        this.pipeName = pipeName;
        this.queryService = queryService ?? (() => WindowsServiceStatus.Query());
    }

    public Task<CommandResult> ProbeAsync(CancellationToken cancellationToken)
        => SendAsync(null, false, true, cancellationToken);
    public Task<CommandResult> ExecuteAsync(Com0ComCommand command, CancellationToken cancellationToken)
        => SendAsync(command, false, false, cancellationToken);
    public Task<CommandResult> StopAllAsync(CancellationToken cancellationToken)
        => SendAsync(null, true, false, cancellationToken);

    private async Task<CommandResult> SendAsync(Com0ComCommand? command, bool stopAll, bool ping, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = command?.Operation ?? (stopAll ? Com0ComOperation.RemovePair : Com0ComOperation.InstallDriver);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        try
        {
            var status = queryService();
            if (!status.Installed) return Failed("The Com0ComSharp management service is not installed.", FailureKind.NotInstalled);
            // Allow automatic service startup to complete after a reboot.
            while (status.State == 2)
            {
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                status = queryService();
            }
            if (!status.Running) return Failed("The installed Com0ComSharp management service is stopped. Start the Windows service and retry.", FailureKind.HelperFailed);

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)options.Timeout.TotalMilliseconds, deadline.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the management pipe server.");
            // Re-query after connecting, so a stale PID from a service restart is
            // never trusted. Only SCM's running, dedicated service process counts.
            status = queryService();
            if (!status.Running || (status.ServiceType & 0x10) == 0 || status.ProcessId != serverPid)
                return Failed("Rejected the pipe server: its PID does not match the running Com0ComSharp Windows service.", FailureKind.HelperFailed);

            var request = new ManagementBrokerRequest(package.Fingerprint().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase), command, stopAll, options.AllowLegacyDriver, ping);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            await RuntimeCompatibility.WaitAsync(WriteRequestAsync(writer, request), deadline.Token).ConfigureAwait(false);
            var response = await RuntimeCompatibility.ReadBoundedLineAsync(reader, 1024 * 1024, deadline.Token).ConfigureAwait(false);
            var result = response is null
                ? Failed("The management service closed the connection without a result.", FailureKind.HelperFailed)
                : JsonSerializer.Deserialize<CommandResult>(response) ?? throw new InvalidDataException("The management service returned an empty response.");
            if (result.Output is null || !Enum.IsDefined(typeof(FailureKind), result.Failure))
                return Failed("The management service returned an invalid result.", FailureKind.HelperFailed);
            if (ping && (result.Success || result.Failure == FailureKind.ElevationRequired)
                && result.BrokerProtocolVersion != ManagementBrokerProtocol.Version)
                return Failed("The installed broker needs a one-time update from the matching application helpers.", FailureKind.NotInstalled);
            if (result.Success && result.Operation != operation)
                return Failed("The management service returned a result for a different operation.", FailureKind.HelperFailed);
            return result with { Operation = operation };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failed("The installed management service did not respond before the timeout.", FailureKind.TimedOut); }
        catch (TimeoutException e) { return Failed("Cannot connect to the installed management service. " + e.Message, FailureKind.TimedOut); }
        catch (Win32Exception e) { return Failed(e.Message + " (Windows error " + e.NativeErrorCode + ").", e.NativeErrorCode == 5 ? FailureKind.AccessDenied : FailureKind.HelperFailed); }
        catch (UnauthorizedAccessException e) { return Failed(e.Message, FailureKind.AccessDenied); }
        catch (System.Security.SecurityException e) { return Failed(e.Message, FailureKind.AccessDenied); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        { return Failed("The installed management service failed. " + e.Message, FailureKind.HelperFailed); }

        CommandResult Failed(string message, FailureKind failure) => new(operation, -1, message, failure);
    }

    private static async Task<bool> WriteRequestAsync(StreamWriter writer, ManagementBrokerRequest request)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
}
