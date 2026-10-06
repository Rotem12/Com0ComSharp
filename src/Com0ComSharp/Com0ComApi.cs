using System.Globalization;

namespace Com0ComSharp;

/// <summary>Simple, VSPE-style Connector and Pair management. Changes throw Com0ComException on failure; created ports persist until explicitly destroyed.</summary>
public sealed class Com0ComApi
{
    private readonly Func<IReadOnlyList<VirtualPortPair>> getPairs;
    /// <summary>The detailed API for advanced settings, batches and diagnostic results.</summary>
    public Com0ComClient Client { get; }

    /// <summary>Opens native files and automatically locates a companion helper beside the application, when present.</summary>
    public Com0ComApi(string driverDirectory, ClientOptions? options = null)
        : this(new Com0ComClient(DriverPackage.Open(driverDirectory), DiscoverHelper(options))) { }

    public Com0ComApi(Com0ComClient client) : this(client, WindowsDiagnostics.GetPairs) { }

    internal Com0ComApi(Com0ComClient client, Func<IReadOnlyList<VirtualPortPair>> getPairs)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        this.getPairs = getPairs;
    }

    public CommandResult InstallDriver() => InstallDriverAsync().GetAwaiter().GetResult();
    public async Task<CommandResult> InstallDriverAsync(CancellationToken cancellationToken = default)
        => Check(await Client.InstallDriverAsync(cancellationToken).ConfigureAwait(false));

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
            return CreateDevice("COM" + fields[0], fields[1] == "1");
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
            return CreateDeviceAsync("COM" + fields[0], fields[1] == "1", cancellationToken);
        }
        var command = ParseVspePair(name, initString);
        return CreatePairAsync(command.PortA!.PortName!, command.PortB!.PortName!, command.PortA.EmulateBaudRate == true, cancellationToken);
    }

    /// <summary>Accepts VSPE's basic Pair initialization string: "21;22;0" (COM numbers; baud emulation 0/1).</summary>
    internal static Com0ComCommand ParseVspePair(string name, string initString)
    {
        if (!string.Equals(name, "Pair", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("com0com supports Pair devices. VSPE Connector, Splitter and network devices have different behavior and are not supported.");
        if (initString is null) throw new ArgumentNullException(nameof(initString));
        var fields = initString.Split(';');
        if (fields.Length != 3 || fields[2] is not ("0" or "1"))
            throw new ArgumentException("Use Pair initialization \"21;22;0\": two COM numbers and baud-rate emulation 0 or 1. Extended VSPE settings are unsupported.", nameof(initString));
        return Com0ComCommand.CreateNamedPair("COM" + fields[0], "COM" + fields[1], fields[2] == "1");
    }

    public CommandResult DestroyDevice(int deviceIndex) => DestroyDeviceAsync(deviceIndex).GetAwaiter().GetResult();
    public async Task<CommandResult> DestroyDeviceAsync(int deviceIndex, CancellationToken cancellationToken = default)
        => Check(WindowsDiagnostics.IsAdministrator()
            ? await Client.DestroyPairAsync(deviceIndex, cancellationToken).ConfigureAwait(false)
            : await ManagementBrokerClient.ExecuteAsync(Client.Package, Com0ComCommand.RemovePair(deviceIndex), Client.Options.AllowLegacyDriver, cancellationToken).ConfigureAwait(false));
    public CommandResult DestroyPair(int pairIndex) => DestroyDevice(pairIndex);
    public Task<CommandResult> DestroyPairAsync(int pairIndex, CancellationToken cancellationToken = default) => DestroyDeviceAsync(pairIndex, cancellationToken);

    /// <summary>Removes the shared driver and every com0com pair on this PC. Requires administrator approval.</summary>
    public CommandResult UninstallDriver() => UninstallDriverAsync().GetAwaiter().GetResult();
    public async Task<CommandResult> UninstallDriverAsync(CancellationToken cancellationToken = default)
        => Check(await Client.UninstallDriverAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Destroys all com0com pairs on this PC and leaves the shared driver installed.</summary>
    public CommandResult Stop() => StopAsync().GetAwaiter().GetResult();
    public async Task<CommandResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!WindowsDiagnostics.IsAdministrator())
            return Check(await ManagementBrokerClient.StopAllAsync(Client.Package, Client.Options.AllowLegacyDriver, cancellationToken).ConfigureAwait(false));
        foreach (var pair in GetDevices())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Client.DestroyPairAsync(pair.Index, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return Check(result);
        }
        return new(Com0ComOperation.RemovePair, 0, "All com0com pairs were removed; the driver remains installed.", FailureKind.None);
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
        => Check(await Client.ChangePortAsync(endpointId, settings, cancellationToken).ConfigureAwait(false));

    public BatchResult SetBaudRateEmulation(int deviceIndex, bool enabled)
        => SetBaudRateEmulationAsync(deviceIndex, enabled).GetAwaiter().GetResult();
    public async Task<BatchResult> SetBaudRateEmulationAsync(int deviceIndex, bool enabled, CancellationToken cancellationToken = default)
    {
        var pair = GetDeviceInfo(deviceIndex);
        if (pair.A is null || pair.B is null) throw new InvalidOperationException("Both pair endpoints are required.");
        var settings = new PortSettings(emulateBaudRate: enabled);
        var result = await Client.ExecuteBatchAsync(new[] { Com0ComCommand.ChangePort(pair.A.Id, settings), Com0ComCommand.ChangePort(pair.B.Id, settings) }, cancellationToken).ConfigureAwait(false);
        result.ThrowIfFailed();
        return result;
    }

    private static CommandResult Check(CommandResult result) { result.ThrowIfFailed(); return result; }

    private async Task<CommandResult> CreateWithDriverIfNeededAsync(Com0ComCommand create, CancellationToken cancellationToken)
    {
        // Registry/package discovery is read-only. Stage the driver only when no
        // complete com0com package is registered, then create the port in the
        // same typed plan so the elevation helper asks only once.
        if (WindowsDiagnostics.FindInstalledPackage() is not null)
            return Check(WindowsDiagnostics.IsAdministrator()
                ? await Client.ExecuteAsync(create, cancellationToken).ConfigureAwait(false)
                : await ManagementBrokerClient.ExecuteAsync(Client.Package, create, Client.Options.AllowLegacyDriver, cancellationToken).ConfigureAwait(false));

        if (Client.Options.Elevation == ElevationMode.Prompt && Client.Options.ElevationHelperPath is null && !WindowsDiagnostics.IsAdministrator())
            throw new Com0ComException(new(create.Operation, 740,
                "Deploy the matching Com0ComSharp.Tool.exe beside the application for one UAC approval covering driver installation and port creation.",
                FailureKind.ElevationRequired));

        var batch = await Client.ExecuteBatchAsync(new[] { Com0ComCommand.InstallDriver(), create }, cancellationToken).ConfigureAwait(false);
        foreach (var result in batch.Results)
        {
            if (!result.Success) result.ThrowIfFailed();
            if (result.RebootRequired)
                throw new InvalidOperationException("Driver installation requires a Windows restart before the port can be created. Restart, then retry the operation.");
        }
        if (batch.Results.Count < 2)
        {
            if (batch.Results.Count == 0) throw new InvalidOperationException("The elevation helper did not complete driver installation.");
            throw new InvalidOperationException("The elevation helper stopped before creating the port. Inspect the driver result before retrying.");
        }
        return Check(batch.Results[1]);
    }

    private static ClientOptions DiscoverHelper(ClientOptions? options)
    {
        options ??= new ClientOptions();
        if (options.ElevationHelperPath is not null || options.Elevation != ElevationMode.Prompt) return options;
        var helper = Path.Combine(AppContext.BaseDirectory, "Com0ComSharp.Tool.exe");
        return File.Exists(helper) ? options with { ElevationHelperPath = helper } : options;
    }
}
