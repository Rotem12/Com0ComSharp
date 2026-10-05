namespace Com0ComSharp;

public sealed class Com0ComClient
{
    private readonly ICom0ComCommandRunner runner;
    public DriverPackage Package { get; }
    public ClientOptions Options { get; }

    public Com0ComClient(DriverPackage package, ClientOptions? options = null, ICom0ComCommandRunner? commandRunner = null)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Options = options ?? new();
        if (Options.Timeout <= TimeSpan.Zero || Options.Timeout > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be greater than zero and at most 30 minutes.");
        runner = commandRunner ?? new WindowsCommandRunner();
    }

    public Task<CommandResult> InstallDriverAsync(CancellationToken cancellationToken = default) => ExecuteAsync(Com0ComCommand.InstallDriver(), cancellationToken);
    public Task<CommandResult> CreatePairAsync(PortSettings? a = null, PortSettings? b = null, int? pairIndex = null, CancellationToken cancellationToken = default) => ExecuteAsync(Com0ComCommand.CreatePair(a, b, pairIndex), cancellationToken);
    /// <summary>Creates and verifies the requested standard COM names. The result includes the actual allocated pair index.</summary>
    public Task<CommandResult> CreateNamedPairAsync(string portA, string portB, bool emulateBaudRate = false, CancellationToken cancellationToken = default)
        => ExecuteAsync(Com0ComCommand.CreateNamedPair(portA, portB, emulateBaudRate), cancellationToken);
    public Task<CommandResult> DestroyPairAsync(int pairIndex, CancellationToken cancellationToken = default) => ExecuteAsync(Com0ComCommand.RemovePair(pairIndex), cancellationToken);
    public Task<CommandResult> ChangePortAsync(string portId, PortSettings settings, CancellationToken cancellationToken = default) => ExecuteAsync(Com0ComCommand.ChangePort(portId, settings), cancellationToken);
    /// <summary>Removes ALL com0com pairs and driver packages system-wide. Prefer DestroyPairAsync for app-owned pairs.</summary>
    public Task<CommandResult> UninstallDriverAsync(CancellationToken cancellationToken = default) => ExecuteAsync(new() { Operation = Com0ComOperation.UninstallDriver }, cancellationToken);
    public Task<CommandResult> UpdateDriverAsync(CancellationToken cancellationToken = default) => ExecuteAsync(new() { Operation = Com0ComOperation.UpdateDriver }, cancellationToken);
    public Task<CommandResult> ReloadDriverAsync(CancellationToken cancellationToken = default) => ExecuteAsync(new() { Operation = Com0ComOperation.ReloadDriver }, cancellationToken);
    public IReadOnlyList<VirtualPortPair> GetPairs() => WindowsDiagnostics.GetPairs();
    public EnvironmentReport GetEnvironment() => WindowsDiagnostics.InspectEnvironment();

    public async Task<CommandResult> ExecuteAsync(Com0ComCommand command, CancellationToken cancellationToken = default)
    {
        var batch = await ExecuteBatchAsync([command], cancellationToken).ConfigureAwait(false);
        return batch.Results[0];
    }

    /// <summary>Runs sequentially and stops at the first failure. With the helper, a non-admin receives one UAC prompt for the entire batch.</summary>
    public async Task<BatchResult> ExecuteBatchAsync(IEnumerable<Com0ComCommand> commands, CancellationToken cancellationToken = default)
    {
        if (commands is null) throw new ArgumentNullException(nameof(commands));
        var plan = commands.ToArray();
        if (plan.Length > 100) throw new ArgumentException("Use batches of at most 100 operations.");
        foreach (var command in plan) { if (command is null) throw new ArgumentNullException(nameof(command)); command.ToArguments(); }
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Length == 0) return new([], 0);
        ValidatePackagePolicy(Package, plan, Options.AllowLegacyDriver);
        if (Options.ElevationHelperPath is not null && Options.Elevation == ElevationMode.Prompt)
            return await ElevationBroker.RunAsync(Package, plan, Options, cancellationToken).ConfigureAwait(false);
        var results = new List<CommandResult>();
        foreach (var command in plan)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new(command.Operation, -1, "Batch cancelled before this operation started; completed changes remain in place.", FailureKind.Cancelled)); break;
            }
            var result = await runner.RunAsync(Package, command, Options, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.Success || result.RebootRequired) break;
        }
        return new(results, plan.Length);
    }

    internal static void ValidatePackagePolicy(DriverPackage package, IEnumerable<Com0ComCommand> commands, bool allowLegacy)
    {
        if (!commands.Any(c => c.Operation is Com0ComOperation.InstallDriver or Com0ComOperation.CreatePair or Com0ComOperation.CreateNamedPair or Com0ComOperation.UpdateDriver or Com0ComOperation.ReloadDriver)) return;
        var inspection = package.Inspect();
        if (!inspection.IntegrityVerified) throw new InvalidDataException("The driver catalog is untrusted or a driver/INF file does not match its signed catalog.");
        if (!inspection.MicrosoftHardwareSigned && !allowLegacy)
            throw new InvalidOperationException("This is a legacy/third-party signed driver. Windows 11 compatibility is unverified. Set AllowLegacyDriver=true only for an explicit compatibility test or a deployment you have validated; it does not bypass Windows security policy.");
    }
}
