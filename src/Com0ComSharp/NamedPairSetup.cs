using System.ComponentModel;
using System.Text;

namespace Com0ComSharp;

/// <summary>Runs wholly inside the elevated helper, so adaptive naming needs no additional UAC requests.</summary>
internal static class NamedPairSetup
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<CommandResult> RunAsync(Com0ComCommand command,
        Func<Com0ComCommand, CancellationToken, Task<CommandResult>> run,
        Func<IReadOnlyList<VirtualPortPair>> getPairs, Func<IReadOnlyList<string>> getUnavailableNames,
        Func<IReadOnlyList<DeviceStatus>> getDevices,
        CancellationToken cancellationToken)
    {
        command.ToArguments(); // Validate the complete request before mutation.
        var connector = command.Operation == Com0ComOperation.CreateNamedConnector;
        cancellationToken.ThrowIfCancellationRequested();
        var output = new StringBuilder();
        int? createdIndex = null;
        var entered = false;
        CommandResult Result(int code, FailureKind failure, string note = "", bool reboot = false)
            => new(command.Operation, code, output + note, failure, reboot) { CreatedPairIndex = createdIndex };
        try
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var a = ComPortNames.Normalize(command.PortA!.PortName);
            var b = connector ? null : ComPortNames.Normalize(command.PortB!.PortName);
            var unavailable = new HashSet<string>(getUnavailableNames(), StringComparer.OrdinalIgnoreCase);
            if (unavailable.Contains(a) || (b is not null && unavailable.Contains(b)))
                return Result(32, FailureKind.PortNameInUse, "A requested COM name is already present or reserved. No pair was created.");
            var previous = new HashSet<int>(getPairs().Select(p => p.Index));
            var initialB = connector ? command.PortB! : command.PortB! with { PortName = "-" };
            var install = Com0ComCommand.CreatePair(command.PortA with { PortName = "-" }, initialB);
            var first = await run(install, cancellationToken).ConfigureAwait(false);
            output.AppendLine(first.Output);
            // Native install can choose a different ID; never infer it from a snapshot.
            var observed = SetupOutputParser.ParsePairs(first.Output);
            if (observed.Count == 1 && !previous.Contains(observed[0].Index)) createdIndex = observed[0].Index;
            if (!first.Success) return Result(first.ExitCode, first.Failure, reboot: first.RebootRequired);
            if (first.RebootRequired) return Result(first.ExitCode, FailureKind.RebootRequired, "Restart Windows before continuing pair configuration.", true);
            if (!createdIndex.HasValue || observed[0].A is null || observed[0].B is null)
                return Result(-1, FailureKind.ProcessFailed, "Could not identify a newly created pair. No rename was attempted; inspect devices before retrying.");
            var current = getPairs().SingleOrDefault(p => p.Index == createdIndex.Value);
            if (current?.A is null || current.B is null)
                return Result(-1, FailureKind.ProcessFailed, "The new endpoints were not discovered. Inspect driver/device status before retrying.");

            // Convert and name A before converting B. This avoids automatically
            // allocated A/B names blocking one another (including swapped names).
            var endpointsToConfigure = connector
                ? new[] { (Id: current.A.Id, Name: (string?)a) }
                : new[] { (Id: current.A.Id, Name: (string?)a), (Id: current.B.Id, Name: b) };
            foreach (var endpoint in endpointsToConfigure)
            {
                foreach (var settings in new[] { new PortSettings(portName: "COM#"), new PortSettings(realPortName: endpoint.Name) })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var step = await run(Com0ComCommand.ChangePort(endpoint.Id, settings), cancellationToken).ConfigureAwait(false);
                    output.AppendLine(step.Output);
                    if (!step.Success) return Result(step.ExitCode, step.Failure, reboot: step.RebootRequired);
                    if (step.RebootRequired) return Result(step.ExitCode, FailureKind.RebootRequired, "Restart Windows before continuing pair configuration.", true);
                    current = getPairs().SingleOrDefault(p => p.Index == createdIndex.Value);
                    if (current?.A is null || current.B is null)
                        return Result(-1, FailureKind.ProcessFailed, "A pair endpoint disappeared during configuration. Inspect the partial pair before retrying.");
                    var port = endpoint.Id == current?.A?.Id ? current.A : endpoint.Id == current?.B?.Id ? current.B : null;
                    if (port is null || !port.PortName.Equals("COM#", StringComparison.OrdinalIgnoreCase)
                        || (settings.RealPortName is not null && !port.EffectiveName.Equals(endpoint.Name, StringComparison.OrdinalIgnoreCase)))
                        return Result(-1, FailureKind.ProcessFailed, "The requested class/name change was not observed. The partial pair remains; inspect it before retrying.");
                }
            }
            var devices = getDevices();
            var endpoints = new[] { (Id: current!.A!.Id, Name: (string?)a), (Id: current.B!.Id, Name: b) };
            var unhealthy = endpoints.Where(endpoint => !devices.Any(d => string.Equals(d.PortId, endpoint.Id, StringComparison.OrdinalIgnoreCase)
                && (endpoint.Name is null || string.Equals(d.PortName, endpoint.Name, StringComparison.OrdinalIgnoreCase)) && d.Healthy)).Select(p => p.Id).ToArray();
            if (unhealthy.Length != 0)
            {
                var blocked = devices.Any(d => unhealthy.Contains(d.PortId, StringComparer.OrdinalIgnoreCase) && d.ProblemCode is 48 or 52);
                return Result(-1, blocked ? FailureKind.DriverBlocked : FailureKind.ProcessFailed,
                    "The requested COM endpoint and its paired device were not reported started without a device problem. Inspect GetEnvironment() before using the port.");
            }
            return Result(0, FailureKind.None);
        }
        catch (OperationCanceledException)
        {
            return Result(-1, FailureKind.Cancelled, "Pair setup cancelled. Completed changes remain; inspect devices before retrying.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception)
        {
            return Result(-1, e is UnauthorizedAccessException or System.Security.SecurityException ? FailureKind.AccessDenied : FailureKind.ProcessFailed,
                e.Message + " Completed changes may remain; inspect devices before retrying.");
        }
        finally { if (entered) Gate.Release(); }
    }
}
