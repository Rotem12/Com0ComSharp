using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Com0ComSharp;

/// <summary>Runs inside the broker or elevated helper; waits only for the new pair.</summary>
internal static class NamedPairSetup
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<CommandResult> RunAsync(Com0ComCommand command,
        Func<Com0ComCommand, CancellationToken, Task<CommandResult>> run,
        Func<IReadOnlyList<VirtualPortPair>> getPairs, Func<IReadOnlyList<VirtualPortPair>, IReadOnlyList<string>> getUnavailableNames,
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
            var before = getPairs();
            var unavailable = new HashSet<string>(getUnavailableNames(before), StringComparer.OrdinalIgnoreCase);
            if (unavailable.Contains(a) || (b is not null && unavailable.Contains(b)))
                return Result(32, FailureKind.PortNameInUse, "A requested COM name is already present or reserved. No pair was created.");
            var previous = new HashSet<int>(before.Select(p => p.Index));
            var initialB = connector ? command.PortB! : command.PortB! with { PortName = "-" };
            // Native class conversion deletes and reinstalls an endpoint. A can
            // use the correct class immediately; B stays private until A is named.
            var install = Com0ComCommand.CreatePair(command.PortA with { PortName = "COM#" }, initialB) with { WaitSeconds = 0 };
            var first = await run(install, cancellationToken).ConfigureAwait(false);
            output.AppendLine(first.Output);
            // Native install can choose a different ID; never infer it from a snapshot.
            var observed = SetupOutputParser.ParsePairs(first.Output);
            if (observed.Count == 1 && !previous.Contains(observed[0].Index)) createdIndex = observed[0].Index;
            if (!first.Success) return Result(first.ExitCode, first.Failure, reboot: first.RebootRequired);
            if (first.RebootRequired) return Result(first.ExitCode, FailureKind.RebootRequired, "Restart Windows before continuing pair configuration.", true);
            if (!createdIndex.HasValue || observed[0].A is null || observed[0].B is null)
                return Result(-1, FailureKind.ProcessFailed, "Could not identify a newly created pair. No rename was attempted; inspect devices before retrying.");
            VirtualPortPair? ReadPair() => getPairs().SingleOrDefault(p => p.Index == createdIndex.Value);
            bool StandardPort(VirtualPort? port) => port is not null && port.PortName.Equals("COM#", StringComparison.OrdinalIgnoreCase)
                && !port.EffectiveName.Equals("COM#", StringComparison.OrdinalIgnoreCase);
            var current = await ObserveAsync(ReadPair,
                p => StandardPort(p?.A) && p?.B is not null,
                command.WaitSeconds, cancellationToken).ConfigureAwait(false);
            if (current?.A is null || current.B is null)
                return Result(-1, FailureKind.ProcessFailed, "The new endpoints were not discovered. Inspect driver/device status before retrying.");
            if (!StandardPort(current.A))
                return Result(-1, FailureKind.ProcessFailed, "The new endpoint was not installed in the standard COM class. Inspect the partial pair before retrying.");

            async Task<CommandResult?> CheckHealthAsync(VirtualPortPair pair, bool finalNames = false)
            {
                var endpoints = new[] {
                    (Id: pair.A!.Id, Name: finalNames ? (string?)a : pair.A.PortName == "COM#" ? pair.A.EffectiveName : null),
                    (Id: pair.B!.Id, Name: finalNames ? b : pair.B.PortName == "COM#" ? pair.B.EffectiveName : null) };
                string[] Unhealthy(IReadOnlyList<DeviceStatus> snapshot) => endpoints.Where(endpoint => !snapshot.Any(d => string.Equals(d.PortId, endpoint.Id, StringComparison.OrdinalIgnoreCase)
                    && (endpoint.Name is null || string.Equals(d.PortName, endpoint.Name, StringComparison.OrdinalIgnoreCase)) && d.Healthy)).Select(p => p.Id).ToArray();
                var devices = await ObserveAsync(getDevices,
                    snapshot => Unhealthy(snapshot).Length == 0 || snapshot.Any(d => endpoints.Any(p => p.Id.Equals(d.PortId, StringComparison.OrdinalIgnoreCase)) && d.ProblemCode is 10 or 48 or 52),
                    command.WaitSeconds, cancellationToken).ConfigureAwait(false);
                var unhealthy = Unhealthy(devices);
                if (unhealthy.Length == 0) return null;
                var blocked = devices.Any(d => unhealthy.Contains(d.PortId, StringComparer.OrdinalIgnoreCase) && d.ProblemCode is 48 or 52);
                return Result(-1, blocked ? FailureKind.DriverBlocked : FailureKind.ProcessFailed,
                    "The requested COM endpoint and its paired device were not reported started without a device problem. Inspect GetEnvironment() before using the port.");
            }
            var healthFailure = await CheckHealthAsync(current).ConfigureAwait(false);
            if (healthFailure is not null) return healthFailure;

            // Convert and name A before converting B. This avoids automatically
            // allocated A/B names blocking one another (including swapped names).
            var endpointsToConfigure = connector
                ? new[] { (Id: current.A.Id, Name: (string?)a) }
                : new[] { (Id: current.A.Id, Name: (string?)a), (Id: current.B.Id, Name: b) };
            foreach (var endpoint in endpointsToConfigure)
            {
                VirtualPort? FindPort(VirtualPortPair? pair) => endpoint.Id == pair?.A?.Id ? pair.A : endpoint.Id == pair?.B?.Id ? pair.B : null;
                foreach (var settings in new[] { new PortSettings(portName: "COM#"), new PortSettings(realPortName: endpoint.Name) })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool Matches(VirtualPortPair? pair)
                    {
                        var found = FindPort(pair);
                        return pair?.A is not null && pair.B is not null && found is not null
                            && StandardPort(found)
                            && (settings.RealPortName is null || found.EffectiveName.Equals(endpoint.Name, StringComparison.OrdinalIgnoreCase));
                    }
                    if (Matches(current)) continue; // Avoid a restart when the automatic name already matches.
                    var step = await run(Com0ComCommand.ChangePort(endpoint.Id, settings) with { WaitSeconds = 0 }, cancellationToken).ConfigureAwait(false);
                    output.AppendLine(step.Output);
                    if (!step.Success) return Result(step.ExitCode, step.Failure, reboot: step.RebootRequired);
                    if (step.RebootRequired) return Result(step.ExitCode, FailureKind.RebootRequired, "Restart Windows before continuing pair configuration.", true);
                    current = await ObserveAsync(ReadPair, Matches, command.WaitSeconds, cancellationToken).ConfigureAwait(false);
                    if (current?.A is null || current.B is null)
                        return Result(-1, FailureKind.ProcessFailed, "A pair endpoint disappeared during configuration. Inspect the partial pair before retrying.");
                    if (!Matches(current))
                        return Result(-1, FailureKind.ProcessFailed, "The requested class/name change was not observed. The partial pair remains; inspect it before retrying.");
                    healthFailure = await CheckHealthAsync(current).ConfigureAwait(false);
                    if (healthFailure is not null) return healthFailure;
                }
            }
            healthFailure = await CheckHealthAsync(current!, finalNames: true).ConfigureAwait(false);
            if (healthFailure is not null) return healthFailure;
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

    // A global PnP drain can wait for unrelated hardware after every native
    // command. Check this pair instead, with the caller's existing wait limit.
    private static async Task<T> ObserveAsync<T>(Func<T> observe, Func<T, bool> complete, int waitSeconds, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = observe();
            if (complete(snapshot) || timer.Elapsed >= TimeSpan.FromSeconds(waitSeconds)) return snapshot;
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }
}
