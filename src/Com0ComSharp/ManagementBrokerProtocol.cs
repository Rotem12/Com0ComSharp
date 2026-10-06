using System.Text.Json;

namespace Com0ComSharp;

internal sealed record ManagementBrokerRequest(Dictionary<string, string> PackageHashes, Com0ComCommand? Command, bool StopAll, bool AllowLegacy, bool Ping = false);

internal static class ManagementBrokerProtocol
{
    internal const int Version = 3; // Upgrade the installed broker to the faster creation path.

    internal static async Task<CommandResult> ProcessAsync(DriverPackage package, string json,
        Func<Com0ComCommand, bool, CancellationToken, Task<CommandResult>> execute,
        Func<IReadOnlyList<VirtualPortPair>> getPairs, Func<bool> driverStaged, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<ManagementBrokerRequest>(json) ?? throw new InvalidDataException("Empty request.");
        var actual = package.Fingerprint();
        if (request.PackageHashes is null || request.PackageHashes.Count != actual.Count || actual.Any(x => !request.PackageHashes.TryGetValue(x.Key, out var hash) || !string.Equals(hash, x.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The application's driver package differs from the installed broker package. Deploy the matching package and helpers.");
        if ((request.Ping && (request.Command is not null || request.StopAll)) || (request.StopAll && request.Command is not null))
            throw new InvalidDataException("Conflicting management operations.");
        if (request.Ping)
            return (driverStaged()
                ? new CommandResult(Com0ComOperation.InstallDriver, 0, "The driver package and management service are installed.", FailureKind.None)
                : new CommandResult(Com0ComOperation.InstallDriver, 2, "The driver package is absent from Windows DriverStore.", FailureKind.NotInstalled))
                with { BrokerProtocolVersion = Version };
        if (request.StopAll)
        {
            foreach (var pair in getPairs())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var removed = await execute(Com0ComCommand.RemovePair(pair.Index), request.AllowLegacy, cancellationToken).ConfigureAwait(false);
                if (!removed.Success) return removed;
                if (removed.RebootRequired) return removed with { Failure = FailureKind.RebootRequired, Output = removed.Output + " Restart Windows before removing the remaining ports." };
            }
            return new(Com0ComOperation.RemovePair, 0, "All com0com pairs were removed; the driver and broker remain installed.", FailureKind.None);
        }
        var command = request.Command ?? throw new InvalidDataException("A management command is required.");
        if (command.Operation is not (Com0ComOperation.CreateNamedPair or Com0ComOperation.CreateNamedConnector or Com0ComOperation.RemovePair or Com0ComOperation.ChangePort))
            throw new InvalidDataException("The management service accepts only port creation, removal, and configuration.");
        _ = command.ToArguments();
        return await execute(command, request.AllowLegacy, cancellationToken).ConfigureAwait(false);
    }
}
