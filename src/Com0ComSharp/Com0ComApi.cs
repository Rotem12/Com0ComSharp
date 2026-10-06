using System.Globalization;

namespace Com0ComSharp;

/// <summary>Simple, VSPE-style Connector and Pair management. Changes throw Com0ComException on failure; created ports persist until explicitly destroyed.</summary>
public sealed class Com0ComApi
{
    private readonly Func<IReadOnlyList<VirtualPortPair>> getPairs;
    private readonly IManagementBroker broker;
    private readonly Func<CancellationToken, Task<CommandResult>> installComponents;
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    /// <summary>The detailed API for advanced settings, batches and diagnostic results.</summary>
    public Com0ComClient Client { get; }

    /// <summary>Opens native files and automatically locates a companion helper beside the application, when present.</summary>
    public Com0ComApi(string driverDirectory, ClientOptions? options = null)
        : this(new Com0ComClient(DriverPackage.Open(driverDirectory), DiscoverCompanions(options))) { }

    public Com0ComApi(Com0ComClient client) : this(client, WindowsDiagnostics.GetPairs) { }

    internal Com0ComApi(Com0ComClient client, Func<IReadOnlyList<VirtualPortPair>> getPairs,
        IManagementBroker? broker = null, Func<CancellationToken, Task<CommandResult>>? installComponents = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        this.getPairs = getPairs;
        this.broker = broker ?? new ManagementBrokerClient(client.Package, client.Options);
        this.installComponents = installComponents ?? InstallComponentsAsync;
    }

    /// <summary>Checks for both driver and broker, requests elevation once when needed, and installs both components.</summary>
    public CommandResult InstallDriver() => InstallDriverAsync().GetAwaiter().GetResult();
    public async Task<CommandResult> InstallDriverAsync(CancellationToken cancellationToken = default)
    {
        await SetupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var readiness = await broker.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (readiness.Success) return readiness with { Operation = Com0ComOperation.InstallDriver };
            // Only confirmed absence or a protocol update permits setup. A busy,
            // stopped or unauthenticated service must never cause another UAC.
            if (readiness.Failure != FailureKind.NotInstalled) return Check(readiness);
            var result = Check(await installComponents(cancellationToken).ConfigureAwait(false));
            if (result.RebootRequired) return result;
            readiness = await broker.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!readiness.Success)
                return Check(readiness with { Operation = Com0ComOperation.InstallDriver, Output = result.Output + Environment.NewLine + "Setup completed, but readiness verification failed: " + readiness.Output });
            return result;
        }
        finally { SetupGate.Release(); }
    }

    private async Task<CommandResult> InstallComponentsAsync(CancellationToken cancellationToken)
    {
        var brokerPath = Client.Options.ManagementBrokerPath;
        if (string.IsNullOrWhiteSpace(brokerPath) || !File.Exists(brokerPath))
            throw new FileNotFoundException("Deploy the matching Com0ComSharp.Broker.exe beside the application for first-time setup.", brokerPath);
        brokerPath = Path.GetFullPath(brokerPath!);
        if (WindowsDiagnostics.IsAdministrator())
        {
            var hashes = Client.Package.Fingerprint();
            var brokerHash = ManagementBrokerInstaller.HashFile(brokerPath);
            var elevated = new Com0ComClient(Client.Package, Client.Options with { Elevation = ElevationMode.RequireAdministrator, ElevationHelperPath = null });
            var result = Check(await elevated.InstallDriverAsync(cancellationToken).ConfigureAwait(false));
            ManagementBrokerInstaller.Install(Client.Package, brokerPath, brokerHash, hashes);
            return result;
        }
        if (Client.Options.Elevation == ElevationMode.Prompt && Client.Options.ElevationHelperPath is not null)
            return await Client.InstallDriverAsync(cancellationToken).ConfigureAwait(false);
        return new(Com0ComOperation.InstallDriver, 740, "Deploy Com0ComSharp.Tool.exe and enable elevation prompts so Windows can request administrator approval for first-time setup.", FailureKind.ElevationRequired);
    }

    /// <summary>Creates active standard COM ports and returns their stable com0com pair ID. Requires free/unreserved names.</summary>
    public int CreatePair(string portA, string portB, bool emulateBaudRate = false)
        => CreatePairAsync(portA, portB, emulateBaudRate).GetAwaiter().GetResult();
    public async Task<int> CreatePairAsync(string portA, string portB, bool emulateBaudRate = false, CancellationToken cancellationToken = default)
    {
        var command = Com0ComCommand.CreateNamedPair(portA, portB, emulateBaudRate);
        var result = await CreateWithDriverIfNeededAsync(command, cancellationToken).ConfigureAwait(false);
        return result.CreatedPairIndex ?? throw new InvalidDataException("Named pair setup did not return its allocated ID. Inspect devices before retrying.");
    }

    /// <summary>Creates one standard COM port with a hidden paired endpoint. Baud-rate emulation is optional; set the actual rate when opening SerialPort.</summary>
    public int CreateDevice(string portName, bool emulateBaudRate = false)
        => CreateDeviceAsync(portName, emulateBaudRate).GetAwaiter().GetResult();
    public async Task<int> CreateDeviceAsync(string portName, bool emulateBaudRate = false, CancellationToken cancellationToken = default)
    {
        var command = Com0ComCommand.CreateNamedConnector(portName, emulateBaudRate);
        var result = await CreateWithDriverIfNeededAsync(command, cancellationToken).ConfigureAwait(false);
        return result.CreatedPairIndex ?? throw new InvalidDataException("Connector setup did not return its allocated ID. Inspect devices before retrying.");
    }

    /// <summary>Accepts a numeric COM index, such as 21.</summary>
    public int CreateDevice(int portNumber, bool emulateBaudRate = false)
        => CreateDevice("COM" + portNumber.ToString(CultureInfo.InvariantCulture), emulateBaudRate);
    public Task<int> CreateDeviceAsync(int portNumber, bool emulateBaudRate = false, CancellationToken cancellationToken = default)
        => CreateDeviceAsync("COM" + portNumber.ToString(CultureInfo.InvariantCulture), emulateBaudRate, cancellationToken);

    /// <summary>Creates a VSPE Connector from its port index and baud-emulation flag, such as CreateDevice("Connector", "21;1").</summary>
    public int CreateDevice(string name, string initString)
    {
        if (string.Equals(name, "Connector", StringComparison.OrdinalIgnoreCase))
        {
            var fields = initString?.Split(';') ?? throw new ArgumentNullException(nameof(initString));
            if (fields.Length != 2 || fields[1] is not ("0" or "1"))
                throw new ArgumentException("Use Connector initialization \"21;1\": one COM number and baud-rate emulation 0 or 1.", nameof(initString));
            return CreateDevice(fields[0], fields[1] == "1");
        }
        var command = ParseVspePair(name, initString);
        return CreatePair(command.PortA!.PortName!, command.PortB!.PortName!, command.PortA.EmulateBaudRate == true);
    }

    public Task<int> CreateDeviceAsync(string name, string initString, CancellationToken cancellationToken = default)
    {
        if (string.Equals(name, "Connector", StringComparison.OrdinalIgnoreCase))
        {
            var fields = initString?.Split(';') ?? throw new ArgumentNullException(nameof(initString));
            if (fields.Length != 2 || fields[1] is not ("0" or "1"))
                throw new ArgumentException("Use Connector initialization \"21;1\": one COM number and baud-rate emulation 0 or 1.", nameof(initString));
            return CreateDeviceAsync(fields[0], fields[1] == "1", cancellationToken);
        }
        var command = ParseVspePair(name, initString);
        return CreatePairAsync(command.PortA!.PortName!, command.PortB!.PortName!, command.PortA.EmulateBaudRate == true, cancellationToken);
    }

    /// <summary>Accepts VSPE's basic Pair initialization string: "21;22;0" (COM numbers; baud emulation 0/1).</summary>
    internal static Com0ComCommand ParseVspePair(string name, string initString)
    {
        if (!string.Equals(name, "Pair", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Use Pair or Connector. VSPE Splitter and network devices are unsupported.");
        if (initString is null) throw new ArgumentNullException(nameof(initString));
        var fields = initString.Split(';');
        if (fields.Length != 3 || fields[2] is not ("0" or "1"))
            throw new ArgumentException("Use Pair initialization \"21;22;0\": two COM numbers and baud-rate emulation 0 or 1. Extended VSPE settings are unsupported.", nameof(initString));
        return Com0ComCommand.CreateNamedPair(fields[0], fields[1], fields[2] == "1");
    }

    public CommandResult DestroyDevice(int deviceIndex) => DestroyDeviceAsync(deviceIndex).GetAwaiter().GetResult();
    public async Task<CommandResult> DestroyDeviceAsync(int deviceIndex, CancellationToken cancellationToken = default)
    {
        var command = Com0ComCommand.RemovePair(deviceIndex);
        _ = command.ToArguments();
        await EnsureDriverInstalledAsync(cancellationToken).ConfigureAwait(false);
        return Check(await broker.ExecuteAsync(command, cancellationToken).ConfigureAwait(false));
    }
    public CommandResult DestroyPair(int pairIndex) => DestroyDevice(pairIndex);
    public Task<CommandResult> DestroyPairAsync(int pairIndex, CancellationToken cancellationToken = default) => DestroyDeviceAsync(pairIndex, cancellationToken);

    /// <summary>Removes every pair, then removes the shared driver and management service with one administrator approval.</summary>
    public CommandResult UninstallDriver() => UninstallDriverAsync().GetAwaiter().GetResult();
    public async Task<CommandResult> UninstallDriverAsync(CancellationToken cancellationToken = default)
    {
        var elevatedHelperWillRemoveService = Client.Options.Elevation == ElevationMode.Prompt && Client.Options.ElevationHelperPath is not null;
        if (!elevatedHelperWillRemoveService && !WindowsDiagnostics.IsAdministrator() && Client.Options.Elevation == ElevationMode.Prompt)
            throw new Com0ComException(new(Com0ComOperation.UninstallDriver, 740,
                "Deploy the matching Com0ComSharp.Tool.exe so Windows can prompt once to remove the driver and management service.", FailureKind.ElevationRequired));
        if (!elevatedHelperWillRemoveService && WindowsDiagnostics.IsAdministrator()) ManagementBrokerInstaller.StopForDriverRemoval();
        var result = await Client.UninstallDriverAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            if (!elevatedHelperWillRemoveService && WindowsDiagnostics.IsAdministrator()) ManagementBrokerInstaller.RestoreAfterFailedDriverRemoval();
            return Check(result);
        }
        if (!elevatedHelperWillRemoveService && WindowsDiagnostics.IsAdministrator()) ManagementBrokerInstaller.Uninstall();
        return Check(result);
    }

    /// <summary>Destroys all com0com pairs on this PC and leaves the shared driver installed.</summary>
    public CommandResult Stop() => StopAsync().GetAwaiter().GetResult();
    public async Task<CommandResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDriverInstalledAsync(cancellationToken).ConfigureAwait(false);
        return Check(await broker.StopAllAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists all installed com0com pairs. Their IDs can be sparse; enumerate this list rather than 0..count-1.</summary>
    public IReadOnlyList<VirtualPortPair> GetDevices() => getPairs();
    public int GetDevicesCount() => GetDevices().Count;
    public VirtualPortPair GetDeviceInfo(int deviceIndex)
        => GetDevices().SingleOrDefault(p => p.Index == deviceIndex) ?? throw new KeyNotFoundException("No com0com pair has ID " + deviceIndex.ToString(CultureInfo.InvariantCulture) + ".");
    /// <summary>Returns the pair ID owning the given COM number, or -1 when absent.</summary>
    public int GetDeviceIndexByComPortIndex(int comPortIndex)
    {
        var name = ComPortNames.Normalize("COM" + comPortIndex.ToString(CultureInfo.InvariantCulture));
        return GetDevices().FirstOrDefault(p => string.Equals(p.A?.EffectiveName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.B?.EffectiveName, name, StringComparison.OrdinalIgnoreCase))?.Index ?? -1;
    }
    public EnvironmentReport GetEnvironment() => Client.GetEnvironment();
    public CommandResult ChangePort(string endpointId, PortSettings settings) => ChangePortAsync(endpointId, settings).GetAwaiter().GetResult();
    public async Task<CommandResult> ChangePortAsync(string endpointId, PortSettings settings, CancellationToken cancellationToken = default)
    {
        var command = Com0ComCommand.ChangePort(endpointId, settings);
        _ = command.ToArguments();
        await EnsureDriverInstalledAsync(cancellationToken).ConfigureAwait(false);
        return Check(await broker.ExecuteAsync(command, cancellationToken).ConfigureAwait(false));
    }

    public BatchResult SetBaudRateEmulation(int deviceIndex, bool enabled)
        => SetBaudRateEmulationAsync(deviceIndex, enabled).GetAwaiter().GetResult();
    public async Task<BatchResult> SetBaudRateEmulationAsync(int deviceIndex, bool enabled, CancellationToken cancellationToken = default)
    {
        var pair = GetDeviceInfo(deviceIndex);
        if (pair.A is null || pair.B is null) throw new InvalidOperationException("Both pair endpoints are required.");
        var settings = new PortSettings(emulateBaudRate: enabled);
        var commands = new[] { Com0ComCommand.ChangePort(pair.A.Id, settings), Com0ComCommand.ChangePort(pair.B.Id, settings) };
        await EnsureDriverInstalledAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<CommandResult>();
        foreach (var command in commands)
        {
            var changed = await broker.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            results.Add(changed);
            if (!changed.Success || changed.RebootRequired) break;
        }
        var result = new BatchResult(results, commands.Length);
        result.ThrowIfFailed();
        return result;
    }

    private static CommandResult Check(CommandResult result) { result.ThrowIfFailed(); return result; }

    private async Task<CommandResult> CreateWithDriverIfNeededAsync(Com0ComCommand create, CancellationToken cancellationToken)
    {
        _ = create.ToArguments();
        await EnsureDriverInstalledAsync(cancellationToken).ConfigureAwait(false);
        return Check(await broker.ExecuteAsync(create, cancellationToken).ConfigureAwait(false));
    }

    private async Task EnsureDriverInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await InstallDriverAsync(cancellationToken).ConfigureAwait(false);
        if (result.RebootRequired)
            throw new Com0ComException(result with { Failure = FailureKind.RebootRequired, Output = "Restart Windows, then retry this operation. " + result.Output });
    }

    private static ClientOptions DiscoverCompanions(ClientOptions? options)
    {
        options ??= new ClientOptions();
        var baseDirectory = AppContext.BaseDirectory;
        var helper = options.ElevationHelperPath ?? Path.Combine(baseDirectory, "Com0ComSharp.Tool.exe");
        var broker = options.ManagementBrokerPath ?? Path.Combine(baseDirectory, "Com0ComSharp.Broker.exe");
        return options with
        {
            ElevationHelperPath = options.ElevationHelperPath ?? (File.Exists(helper) ? helper : null),
            ManagementBrokerPath = options.ManagementBrokerPath ?? (File.Exists(broker) ? broker : null)
        };
    }
}
