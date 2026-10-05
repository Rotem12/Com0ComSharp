using System.Globalization;
using Com0ComSharp;
using Xunit;

namespace Com0ComSharp.Tests;

public class ContractTests
{
    [Fact]
    public void NoiseIsInvariantAndIncludesAllTypedSettings()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var parameters = new PortSettings
            {
                PortName = "COM#", EmulateBaudRate = true, EmulateOverrun = false, PlugInMode = true,
                ExclusiveMode = true, HiddenMode = false, AllDataBits = true, NoiseProbability = 0.00001m,
                AdditionalReadTotalTimeout = 100, AdditionalReadIntervalTimeout = 200,
                Cts = new(PinSource.LocalDtr, true), Dsr = new(PinSource.RemoteRts), Dcd = new(PinSource.RemoteOpen), Ring = new(PinSource.On, true)
            }.ToParameterString();
            Assert.Contains("EmuNoise=0.00001", parameters);
            Assert.Contains("cts=!ldtr", parameters); Assert.Contains("ri=!on", parameters);
            Assert.Contains("EmuOverrun=no", parameters); Assert.Contains("AddRITO=200", parameters);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("COM2,HiddenMode=yes")]
    [InlineData("COM2 --silent")]
    [InlineData("COM2\" & calc")]
    [InlineData("../file")]
    [InlineData("COM12345678901")]
    [InlineData("")]
    [InlineData("COM2\nremove 0")]
    public void RejectsUnsafeOrUnsupportedPortNames(string name) => Assert.Throws<ArgumentException>(() => new PortSettings { PortName = name }.ToParameterString());

    [Theory]
    [InlineData("CNCA0")]
    [InlineData("CNCB999999")]
    public void AcceptsEndpointIds(string id) => Assert.Contains(id, Com0ComCommand.ChangePort(id, new() { HiddenMode = true }).ToArguments());

    [Theory]
    [InlineData("COM42")]
    [InlineData("CNCA1000000")]
    [InlineData("CNCA0\nremove 1")]
    public void RejectsNonEndpointIds(string id) => Assert.Throws<ArgumentException>(() => Com0ComCommand.ChangePort(id, new()).ToArguments());

    [Fact]
    public void RejectsInstallRealNameInsteadOfSilentlyIgnoringIt() => Assert.Throws<ArgumentException>(() => Com0ComCommand.CreatePair(new() { PortName = "COM#", RealPortName = "COM42" }).ToArguments());

    [Fact]
    public void RejectsSameNamedEndpoints() => Assert.Throws<ArgumentException>(() => Com0ComCommand.CreatePair(new() { PortName = "COM42" }, new() { PortName = "com42" }).ToArguments());

    [Theory]
    [InlineData(-1)]
    [InlineData(1000000)]
    public void RejectsPairIndexOutsideDriverLimit(int number) => Assert.Throws<ArgumentOutOfRangeException>(() => Com0ComCommand.RemovePair(number).ToArguments());

    [Fact]
    public void AllowsAutoAllocatedEndpoints() => Assert.Equal(new[] { "--silent", "install", "PortName=COM#", "PortName=COM#" }, Com0ComCommand.CreatePair(new() { PortName = "COM#" }, new() { PortName = "COM#" }).ToArguments());

    [Fact]
    public void RejectsOutOfRangeNoiseAndPrecision()
    {
        foreach (var probability in new[] { -0.1m, 1m, 0.000000001m })
            Assert.Throws<ArgumentOutOfRangeException>(() => new PortSettings { NoiseProbability = probability }.ToParameterString());
    }

    [Theory]
    [InlineData("COM*")]
    [InlineData("COM?*")]
    public void BusyNamesAllowsWildcards(string pattern) => Assert.Contains(pattern, new Com0ComCommand { Operation = Com0ComOperation.BusyNames, Pattern = pattern }.ToArguments());

    [Fact]
    public void ParserGroupsSparsePairsAndKeepsUnknownParameters()
    {
        var pairs = SetupOutputParser.ParsePairs("status message\r\n CNCA4 PortName=COM#,RealPortName=COM42,FutureFlag=yes\r\n CNCB4 PortName=COM#,RealPortName=COM43\n CNCA9 PortName=CNCA9\n");
        Assert.Equal(2, pairs.Count); Assert.Equal(4, pairs[0].Index);
        Assert.Equal("COM42", pairs[0].A!.EffectiveName); Assert.Equal("yes", pairs[0].A!.Parameters["FutureFlag"]);
        Assert.Null(pairs[1].B);
    }

    [Fact]
    public void ParserUsesLastObservedStateWhenChangePrintsBeforeAndAfter()
    {
        var pairs = SetupOutputParser.ParsePairs("CNCA0 PortName=COM2\nCNCA0 PortName=COM3\n");
        Assert.Equal("COM3", Assert.Single(pairs).A!.EffectiveName);
    }

    [Fact]
    public void ExitCodeZeroDoesNotProveKernelReadiness()
    {
        var result = CommandResult.FromExit(Com0ComOperation.CreatePair, 0, "installed");
        Assert.True(result.Success);
        Assert.False(new DeviceStatus(@"COM0COM\PORT\CNCA0", "port", "CNCA0", "COM42", 52, false).Healthy);
    }

    [Fact]
    public void RecognizesCodeIntegrityErrorFromNativeLog() => Assert.Equal(FailureKind.DriverBlocked, CommandResult.FromExit(Com0ComOperation.CreatePair, 1, "error 0x00000241").Failure);

    [Fact]
    public void DeviceRestartDoesNotMeanWindowsReboot()
    {
        Assert.False(CommandResult.FromExit(Com0ComOperation.ChangePort, 0, "Restarted CNCA0 com0com\\port").RebootRequired);
        Assert.True(CommandResult.FromExit(Com0ComOperation.ChangePort, 1, "\nReboot required.\n").RebootRequired);
    }

    [Fact]
    public void BatchFailureIncludesCompletedOperations()
    {
        var first = new CommandResult(Com0ComOperation.InstallDriver, 0, "staged", FailureKind.None);
        var second = new CommandResult(Com0ComOperation.CreatePair, 1, "blocked", FailureKind.DriverBlocked);
        var batch = new BatchResult([first, second], 3);
        Assert.False(batch.Success); Assert.Equal(second, Assert.Throws<Com0ComException>(batch.ThrowIfFailed).Result);
    }

    [Fact]
    public void NativeReadOnlyDiagnosticsDoNotInvokeElevation()
    {
        var report = WindowsDiagnostics.InspectEnvironment();
        Assert.NotEmpty(report.OperatingSystem); Assert.NotEmpty(report.Architecture);
        Assert.NotNull(WindowsDiagnostics.GetPairs()); Assert.NotNull(WindowsDiagnostics.GetReservedComPortNames());
    }
}
