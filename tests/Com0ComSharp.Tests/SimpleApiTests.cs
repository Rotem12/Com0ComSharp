using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Com0ComSharp.Tests;

public sealed class SimpleApiTests
{
    [Theory]
    [InlineData("21;22;0", false)]
    [InlineData("21;22;1", true)]
    public void VspePairSyntaxMapsNumbersAndBaudEmulation(string initString, bool baud)
    {
        var command = Com0ComApi.ParseVspePair("Pair", initString);
        Assert.Equal(Com0ComOperation.CreateNamedPair, command.Operation);
        Assert.Equal("COM21", command.PortA!.PortName);
        Assert.Equal("COM22", command.PortB!.PortName);
        Assert.Equal(baud, command.PortA.EmulateBaudRate);
        Assert.Equal(baud, command.PortB.EmulateBaudRate);
        var restored = JsonSerializer.Deserialize<Com0ComCommand>(JsonSerializer.Serialize(command))!;
        Assert.Equal(command.ToArguments(), restored.ToArguments());
    }

    [Theory]
    [InlineData("Connector")]
    [InlineData("Splitter")]
    [InlineData("TcpServer")]
    public void RejectsDifferentVspeDeviceSemantics(string device)
        => Assert.Throws<NotSupportedException>(() => Com0ComApi.ParseVspePair(device, "21;22;0"));

    [Theory]
    [InlineData("21;22")]
    [InlineData("21;22;0;1")]
    [InlineData("21;22;yes")]
    [InlineData("0;22;0")]
    [InlineData("4097;22;0")]
    [InlineData("21,EmuOverrun=yes;22;0")]
    [InlineData("21;21;0")]
    public void InvalidVspeSettingsFailBeforeARequest(string settings)
        => Assert.Throws<ArgumentException>(() => Com0ComApi.ParseVspePair("Pair", settings).ToArguments());

    [Fact]
    public async Task NamingUsesActualAllocatedIdAndAvoidsSwappedAutoNames()
    {
        var backend = new PairBackend();
        var result = await backend.CreateAsync("com3", "COM2", true);
        Assert.True(result.Success);
        Assert.Equal(18, result.CreatedPairIndex);
        Assert.Equal(5, backend.Commands.Count);
        Assert.Null(backend.Commands[0].PairIndex);
        Assert.All(backend.Commands.Skip(1), c => Assert.Contains(c.PortId, new[] { "CNCA18", "CNCB18" }));
        var created = backend.Pairs.Single(p => p.Index == 18);
        Assert.Equal("COM3", created.A!.EffectiveName);
        Assert.Equal("COM2", created.B!.EffectiveName);
        Assert.Equal("yes", created.A.Parameters["EmuBR"]);
        Assert.Equal("yes", created.B.Parameters["EmuBR"]);
        Assert.Equal("COM70", backend.Pairs.Single(p => p.Index == 0).A!.EffectiveName);
    }

    [Fact]
    public async Task ReservedNameFailsWithoutCreatingOrReleasingPorts()
    {
        var backend = new PairBackend { Reserved = new[] { "com21" } };
        var result = await backend.CreateAsync();
        Assert.Equal(FailureKind.PortNameInUse, result.Failure);
        Assert.Null(result.CreatedPairIndex);
        Assert.Empty(backend.Commands);
        Assert.Single(backend.Pairs);
    }

    [Fact]
    public async Task SilentNativeRenameFailureIsDetectedAndPartialPairIsRetained()
    {
        var backend = new PairBackend { IgnoreRename = true };
        var result = await backend.CreateAsync();
        Assert.False(result.Success);
        Assert.Equal(FailureKind.ProcessFailed, result.Failure);
        Assert.Equal(18, result.CreatedPairIndex);
        Assert.Equal(3, backend.Commands.Count); // Do not configure B after A failed verification.
        Assert.Equal("CNCB18", backend.Pairs.Single(p => p.Index == 18).B!.EffectiveName);
    }

    [Fact]
    public async Task DisappearingEndpointKeepsPartialResultAndStopsConfiguration()
    {
        var backend = new PairBackend { DropAOnBConversion = true };
        var result = await backend.CreateAsync();
        Assert.Equal(FailureKind.ProcessFailed, result.Failure);
        Assert.Equal(18, result.CreatedPairIndex);
        Assert.Equal(4, backend.Commands.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RebootStopsConfigurationAndReturnsReportedPairId(int step)
    {
        var backend = new PairBackend { RebootStep = step };
        var result = await backend.CreateAsync();
        Assert.False(result.Success);
        Assert.True(result.RebootRequired);
        Assert.Equal(FailureKind.RebootRequired, result.Failure);
        Assert.Equal(18, result.CreatedPairIndex);
        Assert.Equal(step, backend.Commands.Count);
    }

    [Fact]
    public async Task FailureKeepsAllocatedIdAcrossHelperJson()
    {
        var backend = new PairBackend { FailStep = 4 };
        var result = await backend.CreateAsync();
        Assert.Equal(FailureKind.AccessDenied, result.Failure);
        Assert.Equal(4, backend.Commands.Count);
        var batch = new BatchResult(new[] { result }, 1);
        var restored = JsonSerializer.Deserialize<BatchResult>(JsonSerializer.Serialize(batch))!;
        Assert.Equal(18, Assert.Throws<Com0ComException>(restored.ThrowIfFailed).Result.CreatedPairIndex);
    }

    [Theory]
    [InlineData(52, FailureKind.DriverBlocked)]
    [InlineData(10, FailureKind.ProcessFailed)]
    public async Task AssignedNamesDoNotHideWindowsDeviceFailure(uint problem, FailureKind expected)
    {
        var backend = new PairBackend { DeviceProblem = problem };
        var result = await backend.CreateAsync();
        Assert.Equal(expected, result.Failure);
        Assert.Equal(18, result.CreatedPairIndex);
        Assert.Equal(5, backend.Commands.Count);
    }

    [Fact]
    public async Task FinalDeviceNameMustStillMatchTheRequestedName()
    {
        var backend = new PairBackend { DeviceNameMismatch = true };
        var result = await backend.CreateAsync();
        Assert.Equal(FailureKind.ProcessFailed, result.Failure);
        Assert.Equal(18, result.CreatedPairIndex);
    }

    [Fact]
    public async Task CancellationAfterCreationKeepsPairIdAndStartsNoRename()
    {
        using var cancel = new CancellationTokenSource();
        var backend = new PairBackend { AfterCreate = cancel.Cancel };
        var result = await backend.CreateAsync(cancellationToken: cancel.Token);
        Assert.Equal(FailureKind.Cancelled, result.Failure);
        Assert.Equal(18, result.CreatedPairIndex);
        Assert.Single(backend.Commands);
    }

    [Theory]
    [InlineData("CNCA0 PortName=CNCA0\nCNCB0 PortName=CNCB0")]
    [InlineData("status only")]
    [InlineData("CNCA18 PortName=CNCA18\nCNCB18 PortName=CNCB18\nCNCA19 PortName=CNCA19")]
    public async Task AmbiguousOrExistingIdsNeverCauseAnUnrelatedRename(string output)
    {
        var backend = new PairBackend { InstallOutput = output };
        var result = await backend.CreateAsync();
        Assert.False(result.Success);
        Assert.Null(result.CreatedPairIndex);
        Assert.Single(backend.Commands);
    }

    [Fact]
    public async Task AlreadyCancelledRequestNeverStartsNativeWork()
    {
        var backend = new PairBackend();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.CreateAsync(cancellationToken: new CancellationToken(true)));
        Assert.Empty(backend.Commands);
    }

    [Fact]
    public void FacadeLooksUpSparseIdsAndDestroyTargetsExactlyThatPair()
    {
        var directory = RuntimeCompatibility.CreateTemporaryDirectory("Com0ComSharp-facade-test-");
        try
        {
            foreach (var file in DriverPackage.RequiredFiles) File.WriteAllBytes(Path.Combine(directory, file), PeBytes());
            var runner = new RecordingRunner();
            var client = new Com0ComClient(DriverPackage.Open(directory), new ClientOptions(ElevationMode.RequireAdministrator), runner);
            var backend = new PairBackend();
            backend.Pairs = SetupOutputParser.ParsePairs("CNCA43 PortName=COM70\nCNCB43 PortName=COM71");
            var api = new Com0ComApi(client, () => backend.Pairs);
            Assert.Equal(1, api.GetDevicesCount());
            Assert.Equal(43, api.GetDeviceIndexByComPortIndex(70));
            Assert.Equal(-1, api.GetDeviceIndexByComPortIndex(21));
            Assert.Throws<KeyNotFoundException>(() => api.GetDeviceInfo(0));
            api.DestroyDevice(api.GetDeviceInfo(43).Index);
            Assert.Equal(43, Assert.Single(runner.Commands).PairIndex);
            api.SetBaudRateEmulation(43, true);
            Assert.Equal(new[] { "CNCA43", "CNCB43" }, runner.Commands.Skip(1).Select(c => c.PortId));
            runner.Failure = FailureKind.ElevationDenied;
            Assert.Equal(FailureKind.ElevationDenied, Assert.Throws<Com0ComException>(() => api.DestroyDevice(43)).Result.Failure);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class RecordingRunner : ICom0ComCommandRunner
    {
        public readonly List<Com0ComCommand> Commands = new();
        public FailureKind Failure;
        public Task<CommandResult> RunAsync(DriverPackage package, Com0ComCommand command, ClientOptions options, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(new CommandResult(command.Operation, Failure == FailureKind.None ? 0 : 1223, "fixture", Failure));
        }
    }

    private sealed class PairBackend
    {
        public IReadOnlyList<VirtualPortPair> Pairs = SetupOutputParser.ParsePairs("CNCA0 PortName=COM70\nCNCB0 PortName=COM71");
        public readonly List<Com0ComCommand> Commands = new();
        public IReadOnlyList<string> Reserved = new[] { "COM1" };
        public bool IgnoreRename;
        public bool DeviceNameMismatch;
        public bool DropAOnBConversion;
        public int FailStep, RebootStep;
        public uint DeviceProblem;
        public string? InstallOutput;
        public Action? AfterCreate;

        public Task<CommandResult> CreateAsync(string a = "COM21", string b = "COM22", bool baud = false, CancellationToken cancellationToken = default)
            => NamedPairSetup.RunAsync(Com0ComCommand.CreateNamedPair(a, b, baud), RunAsync, () => Pairs, () => Reserved,
                () => Pairs.SelectMany(p => new[] { p.A!, p.B! }).Select(p => new DeviceStatus(p.Id, "fixture", p.Id,
                    p.Id == "CNCB18" && DeviceNameMismatch ? "COM99" : p.EffectiveName,
                    p.Id == "CNCB18" ? DeviceProblem : 0, p.Id != "CNCB18" || DeviceProblem == 0)).ToArray(), cancellationToken);

        private Task<CommandResult> RunAsync(Com0ComCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (Commands.Count == FailStep)
                return Task.FromResult(new CommandResult(command.Operation, 5, "native access failure", FailureKind.AccessDenied));
            if (command.Operation == Com0ComOperation.CreatePair)
            {
                var baud = command.PortA!.EmulateBaudRate == true ? "yes" : "no";
                var text = $"CNCA18 PortName=CNCA18,EmuBR={baud}\nCNCB18 PortName=CNCB18,EmuBR={baud}";
                Pairs = Pairs.Concat(SetupOutputParser.ParsePairs(text)).ToArray();
                AfterCreate?.Invoke();
                return Task.FromResult(new CommandResult(command.Operation, 0, InstallOutput ?? text, FailureKind.None, Commands.Count == RebootStep));
            }
            var pair = Pairs.Single(p => p.Index == 18);
            var old = command.PortId == pair.A!.Id ? pair.A : pair.B!;
            var values = old.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (command.PortA!.PortName == "COM#")
            {
                values["PortName"] = "COM#";
                var used = new HashSet<string>(Pairs.SelectMany(p => new[] { p.A!.EffectiveName, p.B!.EffectiveName }).Concat(Reserved), StringComparer.OrdinalIgnoreCase);
                var number = Enumerable.Range(1, 4096).First(n => !used.Contains("COM" + n.ToString(CultureInfo.InvariantCulture)));
                values["RealPortName"] = "COM" + number.ToString(CultureInfo.InvariantCulture);
            }
            else if (!IgnoreRename)
            {
                var name = command.PortA.RealPortName!;
                var peer = old.Id == pair.A.Id ? pair.B! : pair.A;
                Assert.NotEqual(name, peer.EffectiveName); // Catch a swapped-name collision in the implementation.
                values["RealPortName"] = name;
            }
            var updated = new VirtualPort(old.Id, values["PortName"], values);
            Pairs = Pairs.Select(p => p.Index != 18 ? p : old.Id == p.A!.Id ? p with { A = updated } : p with { B = updated }).ToArray();
            if (DropAOnBConversion && Commands.Count == 4)
                Pairs = Pairs.Select(p => p.Index == 18 ? p with { A = null } : p).ToArray();
            return Task.FromResult(new CommandResult(command.Operation, 0, "native change", FailureKind.None, Commands.Count == RebootStep));
        }
    }

    private static byte[] PeBytes()
    {
        var bytes = new byte[128]; bytes[0] = 0x4d; bytes[1] = 0x5a; bytes[60] = 64; bytes[64] = 0x50; bytes[65] = 0x45;
        var machine = RuntimeInformation.OSArchitecture == Architecture.X64 ? (ushort)0x8664 : (ushort)0x014c;
        bytes[68] = (byte)machine; bytes[69] = (byte)(machine >> 8);
        return bytes;
    }
}
