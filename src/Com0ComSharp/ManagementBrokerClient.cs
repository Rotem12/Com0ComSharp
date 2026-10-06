using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;
using System.Text.Json;

namespace Com0ComSharp;

internal sealed record ManagementBrokerRequest(Dictionary<string, string> PackageHashes, Com0ComCommand? Command, bool StopAll, bool AllowLegacy, bool Ping = false);

/// <summary>Client for the separately installed, machine-wide privileged management service.</summary>
internal static class ManagementBrokerClient
{
    private const string PipeName = "Com0ComSharp.Management.v1";

    internal static Task<CommandResult> ExecuteAsync(DriverPackage package, Com0ComCommand command, bool allowLegacy, CancellationToken cancellationToken)
        => SendAsync(package, command, false, allowLegacy, cancellationToken);

    internal static async Task<BatchResult> ExecuteBatchAsync(DriverPackage package, IReadOnlyList<Com0ComCommand> commands, bool allowLegacy, CancellationToken cancellationToken)
    {
        var results = new List<CommandResult>();
        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteAsync(package, command, allowLegacy, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.Success) break;
        }
        return new(results, commands.Count);
    }

    internal static Task<CommandResult> StopAllAsync(DriverPackage package, bool allowLegacy, CancellationToken cancellationToken)
        => SendAsync(package, null, true, allowLegacy, cancellationToken);

    internal static async Task<bool> IsAvailableAsync(DriverPackage package, bool allowLegacy, CancellationToken cancellationToken)
    {
        var result = await SendAsync(package, null, false, allowLegacy, cancellationToken, ping: true).ConfigureAwait(false);
        return result.Success;
    }

    private static async Task<CommandResult> SendAsync(DriverPackage package, Com0ComCommand? command, bool stopAll, bool allowLegacy, CancellationToken cancellationToken, bool ping = false)
    {
        var request = new ManagementBrokerRequest(package.Fingerprint().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase), command, stopAll, allowLegacy, ping);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid)
                || !IsInstalledBroker(serverPid))
                return new(command?.Operation ?? Com0ComOperation.Help, -1, "Rejected the pipe server because its process does not match the broker executable registered for the Com0ComSharp Windows service.", FailureKind.HelperFailed);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            var response = await reader.ReadLineAsync().ConfigureAwait(false);
            return response is null
                ? new(command?.Operation ?? Com0ComOperation.Help, -1, "The management service closed the connection without a result.", FailureKind.HelperFailed)
                : JsonSerializer.Deserialize<CommandResult>(response) ?? throw new InvalidDataException("The management service returned an empty response.");
        }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            return new(command?.Operation ?? Com0ComOperation.Help, -1,
                "The non-elevated management service is not available. Deploy the matching helper and broker; the first device-management call installs them through Windows elevation. " + e.Message,
                FailureKind.ElevationRequired);
        }
    }

    private static bool IsInstalledBroker(uint processId)
    {
        var serviceImage = GetRegisteredServiceImagePath();
        if (serviceImage is null) return false;
        var process = OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero) return false;
        try
        {
            var path = new StringBuilder(32768);
            var size = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref size)) return false;
            return string.Equals(Path.GetFullPath(path.ToString()), serviceImage, StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(process); }
    }

    private static string? GetRegisteredServiceImagePath()
    {
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Com0ComSharpBroker");
            var commandLine = service?.GetValue("ImagePath") as string;
            if (commandLine is null || commandLine.Trim().Length == 0) return null;
            commandLine = Environment.ExpandEnvironmentVariables(commandLine.Trim());
            var marker = commandLine.IndexOf("Com0ComSharp.Broker.exe", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return null;
            var executable = commandLine.Substring(0, marker + "Com0ComSharp.Broker.exe".Length).Trim().Trim('"');
            if (executable.StartsWith(@"\??\", StringComparison.Ordinal)) executable = executable.Substring(4);
            if (!string.Equals(Path.GetFileName(executable), "Com0ComSharp.Broker.exe", StringComparison.OrdinalIgnoreCase)) return null;
            var fullPath = Path.GetFullPath(executable);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null || !IsProtectedInstallDirectory(directory)) return null;
            return fullPath;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { return null; }
    }

    private static bool IsProtectedInstallDirectory(string directory)
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(Path.Combine(root!, "Com0ComSharp")))
            .Any(root => string.Equals(root, directory, StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder imageName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
