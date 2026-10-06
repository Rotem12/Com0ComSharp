using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Com0ComSharp;

internal sealed record ManagementBrokerRequest(Dictionary<string, string> PackageHashes, Com0ComCommand? Command, bool StopAll, bool AllowLegacy);

/// <summary>Client for the separately installed, machine-wide privileged management service.</summary>
internal static class ManagementBrokerClient
{
    private const string PipeName = "Com0ComSharp.Management.v1";

    internal static Task<CommandResult> ExecuteAsync(DriverPackage package, Com0ComCommand command, bool allowLegacy, CancellationToken cancellationToken)
        => SendAsync(package, command, false, allowLegacy, cancellationToken);

    internal static Task<CommandResult> StopAllAsync(DriverPackage package, bool allowLegacy, CancellationToken cancellationToken)
        => SendAsync(package, null, true, allowLegacy, cancellationToken);

    private static async Task<CommandResult> SendAsync(DriverPackage package, Com0ComCommand? command, bool stopAll, bool allowLegacy, CancellationToken cancellationToken)
    {
        var request = new ManagementBrokerRequest(package.Fingerprint().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase), command, stopAll, allowLegacy);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid)
                || !IsInstalledBroker(serverPid))
                return new(command?.Operation ?? Com0ComOperation.RemovePair, -1, "Rejected a connection that did not come from the installed Com0ComSharp broker service.", FailureKind.HelperFailed);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            var response = await reader.ReadLineAsync().ConfigureAwait(false);
            return response is null
                ? new(command?.Operation ?? Com0ComOperation.RemovePair, -1, "The management service closed the connection without a result.", FailureKind.HelperFailed)
                : JsonSerializer.Deserialize<CommandResult>(response) ?? throw new InvalidDataException("The management service returned an empty response.");
        }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            return new(command?.Operation ?? Com0ComOperation.RemovePair, -1,
                "The non-elevated management service is not available. Install and start Com0ComSharp.Broker once from an elevated setup. " + e.Message,
                FailureKind.ElevationRequired);
        }
    }

    private static bool IsInstalledBroker(uint processId)
    {
        var process = OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero) return false;
        try
        {
            var path = new StringBuilder(32768);
            var size = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref size)) return false;
            var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var expected = Path.Combine(programFiles, "Com0ComSharp", "Com0ComSharp.Broker.exe");
            return string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(process); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder imageName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
