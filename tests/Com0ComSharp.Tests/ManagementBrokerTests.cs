using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Com0ComSharp.Tests;

public sealed class ManagementBrokerTests : IDisposable
{
    private readonly string directory = RuntimeCompatibility.CreateTemporaryDirectory("Com0ComSharp-broker-tests-");
    private readonly DriverPackage package;
    public ManagementBrokerTests()
    {
        var bytes = new byte[128]; bytes[0] = 0x4d; bytes[1] = 0x5a; bytes[60] = 64; bytes[64] = 0x50; bytes[65] = 0x45;
        var machine = RuntimeInformation.OSArchitecture == Architecture.X64 ? (ushort)0x8664 : (ushort)0x014c;
        bytes[68] = (byte)machine; bytes[69] = (byte)(machine >> 8);
        foreach (var name in DriverPackage.RequiredFiles)
            if (name.EndsWith(".inf", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(directory, name), "Unique test INF: " + name);
            else File.WriteAllBytes(Path.Combine(directory, name), bytes);
        package = DriverPackage.Open(directory);
    }

    [Fact]
    public void ScmQueriesRealSystemServicesWithTheCurrentUserToken()
    {
        foreach (var name in new[] { "RpcSs", "EventLog" })
        {
            var status = WindowsServiceStatus.Query(name);
            Assert.True(status.Installed);
            Assert.True(status.Running);
            Assert.NotEqual(0u, status.ProcessId);
        }
        Assert.False(WindowsServiceStatus.Query("Com0ComSharpTests-" + Guid.NewGuid().ToString("N")).Installed);
        Assert.True(Path.IsPathRooted(ManagementBrokerLocation.InstallRoot));
    }

    [Fact]
    public void DriverStoreLookupReadsRealPublishedInfsWithoutElevation()
    {
        var published = Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"), "oem*.inf");
        if (published.Length > 0) Assert.Contains(published, StagedDriverPackage.IsInDriverStore);
    }

    [Fact]
    public void StagedPackageNeedsMatchingPublishedInfsButNoDevicesOrKernelService()
    {
        var infDirectory = Path.Combine(directory, "INF");
        Directory.CreateDirectory(infDirectory);
        Assert.False(StagedDriverPackage.IsInstalled(package, infDirectory, _ => true));
        var index = 0;
        foreach (var name in new[] { "com0com.inf", "cncport.inf", "comport.inf" })
            File.Copy(Path.Combine(directory, name), Path.Combine(infDirectory, "oem" + index++ + ".inf"));
        Assert.True(StagedDriverPackage.IsInstalled(package, infDirectory, _ => true));
        Assert.False(StagedDriverPackage.IsInstalled(package, infDirectory, _ => false));
        File.WriteAllText(Path.Combine(infDirectory, "oem1.inf"), "different driver");
        Assert.False(StagedDriverPackage.IsInstalled(package, infDirectory, _ => true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetupCanReuseItsProtectedSourceOrCopyADeploymentFile(bool sameSource)
    {
        var source = Path.Combine(directory, "setupc.exe");
        var destination = sameSource ? source : Path.Combine(directory, "protected-setupc.exe");
        var expected = File.ReadAllBytes(source);
        ManagementBrokerInstaller.CopyForInstall(source, destination);
        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal(expected, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task SetupWaitsForAStoppedProcessToReleaseTheDestination()
    {
        var source = Path.Combine(directory, "setupc.exe");
        var destination = Path.Combine(directory, "locked-broker.exe");
        File.WriteAllText(destination, "previous executable");
        using var busy = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);
        var copy = Task.Run(() => ManagementBrokerInstaller.CopyForInstall(source, destination));
        await Task.Delay(200);
        Assert.False(copy.IsCompleted);
        busy.Dispose();
        await copy;
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task ApiLifecycleUsesRealPipeTransportAndProtocolWithInMemoryDevices()
    {
        using var host = new PipeHost(package);
        var backend = host.Client();
        var installations = 0;
        var nativeClient = new Com0ComClient(package, new ClientOptions(elevationHelperPath: "must-never-launch.exe"), new ForbiddenRunner());
        var api = new Com0ComApi(nativeClient, () => host.Pairs, backend, _ => { installations++; throw new InvalidOperationException("Unexpected setup"); });
        await api.InstallDriverAsync();
        await api.InstallDriverAsync();
        var first = await api.CreateDeviceAsync("21", true);
        Assert.Equal(first, api.GetDeviceIndexByComPortIndex(21));
        await api.SetBaudRateEmulationAsync(first, false);
        await api.ChangePortAsync("CNCA" + first, new PortSettings(emulateOverrun: true));
        await api.DestroyDeviceAsync(first);
        await api.CreateDeviceAsync("COM22");
        await api.CreateDeviceAsync(23);
        await api.CreateDeviceAsync("Connector", "COM24;1");
        await api.CreateDeviceAsync("Pair", "25;COM26;0");
        await api.StopAsync();
        await api.StopAsync();
        Assert.Empty(host.Pairs);
        Assert.Equal(0, installations);
        Assert.DoesNotContain(host.Commands, c => c.Operation is Com0ComOperation.InstallDriver or Com0ComOperation.UninstallDriver);
        Assert.Contains(host.Commands, c => c.Operation == Com0ComOperation.ChangePort);
        Assert.Equal(5, host.Commands.Count(c => c.Operation is Com0ComOperation.CreateNamedConnector or Com0ComOperation.CreateNamedPair));
    }

    [Fact]
    public async Task ConcurrentFirstUseInstallsOnceAndTheNextApiInstanceFindsIt()
    {
        using var host = new PipeHost(package);
        var installed = false;
        var installations = 0;
        var transport = host.Client(() => installed ? CurrentProcessService() : new WindowsServiceStatus(false));
        Task<CommandResult> Install(CancellationToken _) { installations++; installed = true; return Task.FromResult(Success(Com0ComOperation.InstallDriver)); }
        var client = new Com0ComClient(package, commandRunner: new ForbiddenRunner());
        var api = new Com0ComApi(client, () => host.Pairs, transport, Install);
        await Task.WhenAll(api.CreateDeviceAsync(21), api.CreateDeviceAsync(22));
        var nextRun = new Com0ComApi(client, () => host.Pairs, transport, Install);
        await nextRun.InstallDriverAsync();
        await nextRun.DestroyDeviceAsync(host.Pairs.First().Index);
        await nextRun.StopAsync();
        Assert.Equal(1, installations);
        Assert.Empty(host.Pairs);
    }

    [Theory]
    [InlineData(FailureKind.HelperFailed)]
    [InlineData(FailureKind.TimedOut)]
    [InlineData(FailureKind.AccessDenied)]
    public async Task InstalledServiceFailuresNeverInvokeSetup(FailureKind failure)
    {
        var installations = 0;
        var transport = new StubBroker { Probe = new(Com0ComOperation.InstallDriver, -1, "service failure", failure) };
        var api = new Com0ComApi(new Com0ComClient(package), () => [], transport, _ => { installations++; throw new InvalidOperationException(); });
        Assert.Equal(failure, (await Assert.ThrowsAsync<Com0ComException>(() => api.InstallDriverAsync())).Result.Failure);
        Assert.Equal(failure, (await Assert.ThrowsAsync<Com0ComException>(() => api.CreateDeviceAsync(21))).Result.Failure);
        Assert.Equal(failure, (await Assert.ThrowsAsync<Com0ComException>(() => api.DestroyDeviceAsync(42))).Result.Failure);
        Assert.Equal(failure, (await Assert.ThrowsAsync<Com0ComException>(() => api.StopAsync())).Result.Failure);
        Assert.Equal(0, installations);
        Assert.Equal(0, transport.Executions);
    }

    [Fact]
    public async Task SuccessfulInstallerStillRequiresAWorkingBroker()
    {
        var transport = new StubBroker { Probe = new(Com0ComOperation.InstallDriver, 2, "not staged", FailureKind.NotInstalled) };
        var api = new Com0ComApi(new Com0ComClient(package), () => [], transport, _ => Task.FromResult(Success(Com0ComOperation.InstallDriver)));
        var error = await Assert.ThrowsAsync<Com0ComException>(() => api.CreateDeviceAsync(21));
        Assert.Contains("readiness verification failed", error.Message);
        Assert.Equal(0, transport.Executions);
    }

    [Theory]
    [InlineData(FailureKind.ElevationDenied, false)]
    [InlineData(FailureKind.None, true)]
    public async Task DeniedSetupOrRequiredRestartNeverExecutesDeviceCommands(FailureKind failure, bool reboot)
    {
        var transport = new StubBroker { Probe = new(Com0ComOperation.InstallDriver, 2, "absent", FailureKind.NotInstalled) };
        var setup = new CommandResult(Com0ComOperation.InstallDriver, reboot ? 3010 : 1223, "setup result", failure, reboot);
        var api = new Com0ComApi(new Com0ComClient(package), () => [], transport, _ => Task.FromResult(setup));
        var error = await Assert.ThrowsAsync<Com0ComException>(() => api.CreateDeviceAsync(21));
        Assert.Equal(reboot ? FailureKind.RebootRequired : failure, error.Result.Failure);
        Assert.Equal(reboot, error.Result.RebootRequired);
        Assert.Equal(0, transport.Executions);
    }

    [Fact]
    public async Task InvalidArgumentsFailBeforeInstallation()
    {
        var installations = 0;
        var api = new Com0ComApi(new Com0ComClient(package), () => [], new StubBroker(), _ => { installations++; throw new InvalidOperationException(); });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => api.CreateDeviceAsync("COM0"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => api.DestroyDeviceAsync(-1));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => api.ChangePortAsync("other", new PortSettings(emulateBaudRate: true)));
        Assert.Equal(0, installations);
    }

    [Fact]
    public async Task PipeWithTheWrongServerPidIsRejectedBeforeSendingAnyRequest()
    {
        var name = "Com0ComSharp-test-" + Guid.NewGuid().ToString("N");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(bound.Token);
        var client = new ManagementBrokerClient(package, new ClientOptions(), name, () => CurrentProcessService() with { ProcessId = uint.MaxValue });
        var response = await client.ProbeAsync(bound.Token);
        await connected;
        Assert.Equal(FailureKind.HelperFailed, response.Failure);
        var received = await server.ReadAsync(new byte[1], 0, 1, bound.Token);
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task ServicePidIsRecheckedAfterConnecting()
    {
        var calls = 0;
        using var host = new PipeHost(package);
        var client = host.Client(() => ++calls == 1 ? CurrentProcessService() : CurrentProcessService() with { ProcessId = uint.MaxValue });
        Assert.Equal(FailureKind.HelperFailed, (await client.ProbeAsync(default)).Failure);
        Assert.Equal(2, calls);
        Assert.Empty(host.Commands);
    }

    [Fact]
    public async Task ServiceQueryAccessDeniedIsReportedWithoutConnecting()
    {
        var client = new ManagementBrokerClient(package, new ClientOptions(), queryService: () => throw new Win32Exception(5));
        var response = await client.ProbeAsync(default);
        Assert.Equal(FailureKind.AccessDenied, response.Failure);
        Assert.Contains("Windows error 5", response.Output);
    }

    [Fact]
    public async Task ASharedServiceProcessCannotAuthenticateTheManagementPipe()
    {
        using var host = new PipeHost(package);
        var client = host.Client(() => CurrentProcessService() with { ServiceType = 0x20 });
        Assert.Equal(FailureKind.HelperFailed, (await client.ProbeAsync(default)).Failure);
        Assert.Empty(host.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopReportsFailureOrRestartAndDoesNotClaimAllPairsWereRemoved(bool restart)
    {
        var pairs = SetupOutputParser.ParsePairs("CNCA42 PortName=COM21\nCNCB42 PortName=COM22\nCNCA52 PortName=COM23\nCNCB52 PortName=COM24");
        var attempted = new List<int?>();
        Task<CommandResult> Remove(Com0ComCommand command, bool _, CancellationToken token)
        {
            attempted.Add(command.PairIndex);
            return Task.FromResult(new CommandResult(command.Operation, restart ? 3010 : 5, "native result", restart ? FailureKind.None : FailureKind.AccessDenied, restart));
        }
        var request = new ManagementBrokerRequest(package.Fingerprint().ToDictionary(x => x.Key, x => x.Value), null, true, false);
        var result = await ManagementBrokerProtocol.ProcessAsync(package, JsonSerializer.Serialize(request), Remove, () => pairs, () => true, default);
        Assert.False(result.Success);
        Assert.Equal(restart ? FailureKind.RebootRequired : FailureKind.AccessDenied, result.Failure);
        Assert.Equal(42, Assert.Single(attempted));
    }

    [Fact]
    public async Task StoppedServiceIsAnAvailabilityErrorInsteadOfMissingSetup()
    {
        var client = new ManagementBrokerClient(package, new ClientOptions(), queryService: () => new(true, 1));
        Assert.Equal(FailureKind.HelperFailed, (await client.ProbeAsync(default)).Failure);
    }

    [Fact]
    public async Task StartupPendingWaitsForTheServiceWithoutSetup()
    {
        using var host = new PipeHost(package);
        var calls = 0;
        var client = host.Client(() => ++calls < 3 ? new WindowsServiceStatus(true, 2) : CurrentProcessService());
        Assert.True((await client.ProbeAsync(default)).Success);
        Assert.True(calls >= 4);
    }

    [Fact]
    public async Task SilentPipeTimesOutAndCallerCancellationIsPreserved()
    {
        using var host = new PipeHost(package) { Silent = true };
        var timeoutClient = host.Client(options: new ClientOptions(timeout: TimeSpan.FromMilliseconds(300)));
        Assert.Equal(FailureKind.TimedOut, (await timeoutClient.ProbeAsync(default)).Failure);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client().ProbeAsync(cancel.Token));
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task MalformedRepliesAreFailuresThatDoNotRequestInstallation(string reply)
    {
        using var host = new PipeHost(package) { Reply = reply };
        Assert.Equal(FailureKind.HelperFailed, (await host.Client().ProbeAsync(default)).Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task OldBrokerProtocolRequiresOneUpdate(int? oldVersion)
    {
        using var host = new PipeHost(package) { Reply = JsonSerializer.Serialize(
            Success(oldVersion.HasValue ? Com0ComOperation.InstallDriver : Com0ComOperation.Help) with { BrokerProtocolVersion = oldVersion }) };
        Assert.Equal(FailureKind.NotInstalled, (await host.Client().ProbeAsync(default)).Failure);
        host.Reply = null;
        Assert.True((await host.Client().ProbeAsync(default)).Success);
    }

    [Fact]
    public async Task BrokerRejectsInstallCommandsAndChangedPackagesBeforeNativeWork()
    {
        using var host = new PipeHost(package);
        Assert.Equal(FailureKind.HelperFailed, (await host.Client().ExecuteAsync(Com0ComCommand.InstallDriver(), default)).Failure);
        Assert.Empty(host.Commands);
        var hashes = package.Fingerprint().ToDictionary(x => x.Key, x => x.Value);
        hashes["setupc.exe"] = "changed";
        var json = JsonSerializer.Serialize(new ManagementBrokerRequest(hashes, Com0ComCommand.RemovePair(42), false, false));
        await Assert.ThrowsAsync<InvalidDataException>(() => ManagementBrokerProtocol.ProcessAsync(package, json, host.ExecuteAsync, () => host.Pairs, () => true, default));
        Assert.Empty(host.Commands);
    }

    private static WindowsServiceStatus CurrentProcessService()
    {
        using var process = Process.GetCurrentProcess();
        return new(true, 4, (uint)process.Id, 0x10);
    }
    private static CommandResult Success(Com0ComOperation operation) => new(operation, 0, "fixture", FailureKind.None);
    public void Dispose() => Directory.Delete(directory, true);

    private sealed class ForbiddenRunner : ICom0ComCommandRunner
    {
        public Task<CommandResult> RunAsync(DriverPackage package, Com0ComCommand command, ClientOptions options, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Device management must use the broker, never the local elevation runner.");
    }
    private sealed class StubBroker : IManagementBroker
    {
        internal CommandResult Probe = Success(Com0ComOperation.InstallDriver);
        internal int Executions;
        public Task<CommandResult> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Probe);
        public Task<CommandResult> ExecuteAsync(Com0ComCommand command, CancellationToken cancellationToken) { Executions++; return Task.FromResult(Success(command.Operation)); }
        public Task<CommandResult> StopAllAsync(CancellationToken cancellationToken) { Executions++; return Task.FromResult(Success(Com0ComOperation.RemovePair)); }
    }

    // Real Windows named pipes and the production serializer/protocol. Native
    // device commands are recorded in memory; no UAC or driver changes are made.
    private sealed class PipeHost : IDisposable
    {
        private readonly DriverPackage package;
        private readonly string name = "Com0ComSharp-test-" + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        private readonly Task server;
        internal readonly List<Com0ComCommand> Commands = new();
        internal IReadOnlyList<VirtualPortPair> Pairs = [];
        internal bool Silent;
        internal string? Reply;
        private int nextId = 42;

        internal PipeHost(DriverPackage package) { this.package = package; server = ServeAsync(); }
        internal ManagementBrokerClient Client(Func<WindowsServiceStatus>? status = null, ClientOptions? options = null)
            => new(package, options ?? new ClientOptions(), name, status ?? CurrentProcessService);

        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                    var request = await RuntimeCompatibility.ReadBoundedLineAsync(reader, 65536, stop.Token);
                    if (request is null) continue;
                    if (Silent)
                    {
                        var received = await pipe.ReadAsync(new byte[1], 0, 1, stop.Token); // Wait until the timed-out/cancelled client closes.
                        if (received != 0) throw new InvalidDataException("Unexpected second request.");
                        continue;
                    }
                    CommandResult result;
                    try { result = await ManagementBrokerProtocol.ProcessAsync(package, request, ExecuteAsync, () => Pairs, () => true, stop.Token); }
                    catch (InvalidDataException e) { result = new(Com0ComOperation.Help, -1, e.Message, FailureKind.HelperFailed); }
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    await writer.WriteLineAsync(Reply ?? JsonSerializer.Serialize(result));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (IOException) { }
            }
        }

        internal Task<CommandResult> ExecuteAsync(Com0ComCommand command, bool allowLegacy, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (command.Operation is Com0ComOperation.CreateNamedConnector or Com0ComOperation.CreateNamedPair)
            {
                var id = nextId++;
                Pairs = Pairs.Concat(SetupOutputParser.ParsePairs($"CNCA{id} PortName={command.PortA!.PortName}\nCNCB{id} PortName={command.PortB?.PortName ?? "CNCB" + id}")).ToArray();
                return Task.FromResult(Success(command.Operation) with { CreatedPairIndex = id });
            }
            if (command.Operation == Com0ComOperation.RemovePair) Pairs = Pairs.Where(p => p.Index != command.PairIndex).ToArray();
            return Task.FromResult(Success(command.Operation));
        }
        public void Dispose()
        {
            stop.Cancel();
            server.GetAwaiter().GetResult();
            stop.Dispose();
        }
    }
}
