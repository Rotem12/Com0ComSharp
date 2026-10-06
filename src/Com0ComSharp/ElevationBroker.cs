using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Com0ComSharp;

/// <summary>Protocol used by the companion tool to execute validated operations after Windows elevation.</summary>
public static class ElevationBroker
{
    private sealed record Request(string Directory, Dictionary<string, string> Hashes, Com0ComCommand[] Commands, bool AllowLegacy, int TimeoutMilliseconds, string? ManagementBrokerPath, string? ManagementBrokerHash);

    internal static async Task<BatchResult> RunAsync(DriverPackage package, Com0ComCommand[] commands, ClientOptions options, CancellationToken cancellationToken)
    {
        var helper = Path.GetFullPath(options.ElevationHelperPath!);
        if (!File.Exists(helper)) throw new FileNotFoundException("Publish Com0ComSharp.Tool and set ElevationHelperPath to its .exe.", helper);
        var hashes = package.Fingerprint().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var brokerPath = options.ManagementBrokerPath is null ? null : Path.GetFullPath(options.ManagementBrokerPath);
        var brokerHash = brokerPath is not null && File.Exists(brokerPath) ? ManagementBrokerInstaller.HashFile(brokerPath) : null;
        var request = new Request(package.DirectoryPath, hashes, commands, options.AllowLegacyDriver, checked((int)options.Timeout.TotalMilliseconds), brokerPath, brokerHash);
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
        if (payload.Length > 24000) throw new ArgumentException("This plan exceeds the Windows command-line limit; use a smaller batch.");
        var name = "Com0ComSharp-" + Guid.NewGuid().ToString("N");
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        acl.AddAccessRule(new(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = RuntimeCompatibility.CreateResultPipe(name, acl);
        var start = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(helper)! };
        RuntimeCompatibility.SetArguments(start, new[] { "--broker", name, payload });
        Process? process = null;
        CommandResult Failure(FailureKind kind, int code, string text) => new(commands[0].Operation, code, text, kind);
        try
        {
            try { process = await Task.Run(() => Process.Start(start), CancellationToken.None).ConfigureAwait(false); }
            catch (Win32Exception e) { return new([Failure(e.NativeErrorCode == 1223 ? FailureKind.ElevationDenied : FailureKind.HelperFailed, e.NativeErrorCode, e.Message)], commands.Length); }
            if (process is null) return new([Failure(FailureKind.HelperFailed, -1, "Windows did not start the elevation helper.")], commands.Length);
            // Each operation has its own timeout; allow the complete batch plus startup time.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.Timeout.TotalMilliseconds * commands.Length + 30000));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var connect = pipe.WaitForConnectionAsync(linked.Token);
            var exit = RuntimeCompatibility.WaitForExitAsync(process, linked.Token);
            if (await Task.WhenAny(connect, exit).ConfigureAwait(false) == exit && !pipe.IsConnected)
                return new([Failure(FailureKind.HelperFailed, process.ExitCode, "Elevation helper exited before connecting. Check installed .NET runtime and application-control policy.")], commands.Length);
            await connect.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid) || pid != process.Id)
                return new([Failure(FailureKind.HelperFailed, -1, "Rejected an unexpected process connecting to the result pipe.")], commands.Length);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            var response = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await RuntimeCompatibility.ReadTextAsync(reader, buffer, linked.Token).ConfigureAwait(false)) != 0)
            {
                response.Append(buffer, 0, count);
                if (response.Length > 8_000_000) throw new InvalidDataException("The helper response exceeded its size limit.");
            }
            await exit.ConfigureAwait(false);
            return JsonSerializer.Deserialize<BatchResult>(response.ToString()) ?? throw new InvalidDataException("Empty helper response.");
        }
        catch (OperationCanceledException)
        {
            // A standard user cannot reliably terminate an elevated process. Do not report rollback or retry automatically.
            return new([Failure(cancellationToken.IsCancellationRequested ? FailureKind.Cancelled : FailureKind.TimedOut, -1,
                "Stopped waiting for the helper. Completed changes remain; the helper may still be running. Inspect devices before retrying.")], commands.Length);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        { return new([Failure(FailureKind.HelperFailed, -1, e.Message + " Inspect devices before retrying.")], commands.Length); }
        finally { process?.Dispose(); }
    }

    /// <summary>Entry point for the published companion executable; validates every operation and package fingerprint again after UAC.</summary>
    public static async Task<int> RunElevatedAsync(string pipeName, string payload)
    {
        WindowsDiagnostics.EnsureWindows();
        if (!WindowsDiagnostics.IsAdministrator() || !Regex.IsMatch(pipeName, @"\ACom0ComSharp-[a-f0-9]{32}\z") || payload.Length > 24000) return 2;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30000).ConfigureAwait(false);
        using var operationCancellation = new CancellationTokenSource();
        using var stopMonitor = new CancellationTokenSource();
        var monitor = MonitorParentAsync(pipe, operationCancellation, stopMonitor.Token);
        BatchResult result = new([], 1);
        var activeOperation = Com0ComOperation.Help;
        var brokerStopped = false;
        var count = 1;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(Convert.FromBase64String(payload)) ?? throw new InvalidDataException("Empty request.");
            count = request.Commands.Length;
            activeOperation = request.Commands.FirstOrDefault()?.Operation ?? Com0ComOperation.Help;
            if (count is < 1 or > 100 || request.TimeoutMilliseconds is < 1 or > 1800000) throw new InvalidDataException("Invalid request limits.");
            foreach (var command in request.Commands) command.ToArguments();
            var package = DriverPackage.Open(request.Directory);
            var actual = package.Fingerprint();
            if (request.Hashes.Count != actual.Count || actual.Any(x => !request.Hashes.TryGetValue(x.Key, out var expected) || !expected.Equals(x.Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The native package changed before elevation completed.");
            if (request.Commands.Any(c => c.Operation == Com0ComOperation.InstallDriver))
            {
                if (request.ManagementBrokerPath is null || request.ManagementBrokerHash is null)
                    throw new InvalidDataException("InstallDriver requires the matching Com0ComSharp.Broker.exe beside the application.");
                ManagementBrokerInstaller.VerifyBrokerForInstall(request.ManagementBrokerPath, request.ManagementBrokerHash);
            }
            if (request.Commands.Any(c => c.Operation == Com0ComOperation.UninstallDriver))
            {
                ManagementBrokerInstaller.StopForDriverRemoval();
                brokerStopped = true;
            }
            var client = new Com0ComClient(package, new() { Elevation = ElevationMode.RequireAdministrator, AllowLegacyDriver = request.AllowLegacy, Timeout = TimeSpan.FromMilliseconds(request.TimeoutMilliseconds) });
            result = await client.ExecuteBatchAsync(request.Commands, operationCancellation.Token).ConfigureAwait(false);
            if (result.Results.Any(r => r.Operation == Com0ComOperation.InstallDriver && r.Success))
            {
                activeOperation = Com0ComOperation.InstallDriver;
                ManagementBrokerInstaller.Install(package, request.ManagementBrokerPath!, request.ManagementBrokerHash!, request.Hashes);
            }
            if (result.Results.Any(r => r.Operation == Com0ComOperation.UninstallDriver && r.Success))
            {
                activeOperation = Com0ComOperation.UninstallDriver;
                ManagementBrokerInstaller.Uninstall();
                brokerStopped = false;
            }
            else if (brokerStopped)
            {
                ManagementBrokerInstaller.RestoreAfterFailedDriverRemoval();
                brokerStopped = false;
            }
        }
        catch (Exception e)
        {
            if (brokerStopped)
            {
                try { ManagementBrokerInstaller.RestoreAfterFailedDriverRemoval(); } catch { }
            }
            var completed = result.Results.LastOrDefault(r => r.Operation == activeOperation);
            var failure = (completed ?? new CommandResult(activeOperation, -1, "", FailureKind.HelperFailed))
                with { ExitCode = -1, Failure = FailureKind.HelperFailed, Output = (completed?.Output ?? "") + Environment.NewLine + e.Message };
            result = new([failure], count);
        }
        finally
        {
            stopMonitor.Cancel();
            await monitor.ConfigureAwait(false);
        }
        try
        {
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            await writer.WriteAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            return result.Success ? 0 : 1;
        }
        catch (IOException) { return 1; } // Parent cancelled/closed; avoid another privileged operation.
    }

    private static async Task MonitorParentAsync(NamedPipeClientStream pipe, CancellationTokenSource operationCancellation, CancellationToken stop)
    {
        try
        {
            await RuntimeCompatibility.WaitAsync(pipe.ReadAsync(new byte[1], 0, 1, stop), stop).ConfigureAwait(false);
            operationCancellation.Cancel(); // EOF or an explicit cancellation byte.
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) { operationCancellation.Cancel(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
}
