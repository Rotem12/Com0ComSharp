using System.Runtime.InteropServices;
using Com0ComSharp;
using Xunit;

namespace Com0ComSharp.Tests;

public class FastWindowsDevicesTests
{
    [Fact]
    public void ExistingCommandConstructorRemainsBinaryCompatible()
        => Assert.NotNull(typeof(Com0ComCommand).GetConstructor(new[] { typeof(Com0ComOperation), typeof(int?), typeof(string),
            typeof(PortSettings), typeof(PortSettings), typeof(string), typeof(bool), typeof(int) }));

    [Theory]
    [InlineData(true, "COM31", "COM21", "COM31")]
    [InlineData(true, null, "COM21", null)]
    [InlineData(true, "COM#", "COM21", null)]
    [InlineData(true, "COM9999", "COM21", null)]
    [InlineData(false, null, "COM21", "COM21")]
    public void RenameDoesNotProtectAnObsoleteReservation(bool parametersExist, string? current, string? original, string? expected)
        => Assert.Equal(expected, FastWindowsDevices.ReservationName(parametersExist, current, original));

    [Fact]
    public void NativeStructuresMatchWindowsAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 32 : 28, Marshal.SizeOf<FastWindowsDevices.DeviceInfo>());
        Assert.Equal(IntPtr.Size == 8 ? 584 : 556, Marshal.SizeOf<FastWindowsDevices.InstallParameters>());
        Assert.Equal(1568, Marshal.SizeOf<FastWindowsDevices.DriverInfo>());
    }

    [Fact]
    public void DriverParametersKeepTheirNativeTypesAndBitValues()
    {
        var values = FastWindowsDevices.SettingsValues(new PortSettings(portName: "COM21", emulateBaudRate: true,
            hiddenMode: false, noiseProbability: 0.99999999m, additionalReadTotalTimeout: uint.MaxValue,
            cts: new(PinSource.LocalDtr, true), dsr: new(PinSource.RemoteOpen), ring: new(PinSource.On)));
        Assert.Equal("COM21", values["PortName"]);
        Assert.Equal(1, values["EmuBR"]); Assert.Equal(0, values["HiddenMode"]);
        Assert.Equal(99999999, values["EmuNoise"]); Assert.Equal(-1, values["AddRTTO"]);
        Assert.Equal(unchecked((int)0x80000200), values["cts"]);
        Assert.Equal(0x80, values["dsr"]); Assert.Equal(0x10000000, values["ri"]);
        Assert.False(values.ContainsKey("EmuOverrun"));
        Assert.False(FastWindowsDevices.SettingsValues(new(portName: "-")).ContainsKey("PortName"));
    }

    [Theory]
    [InlineData(PinSource.RemoteRts, 1)]
    [InlineData(PinSource.RemoteDtr, 2)]
    [InlineData(PinSource.RemoteOut1, 4)]
    [InlineData(PinSource.RemoteOut2, 8)]
    [InlineData(PinSource.RemoteOpen, 0x80)]
    [InlineData(PinSource.LocalRts, 0x100)]
    [InlineData(PinSource.LocalDtr, 0x200)]
    [InlineData(PinSource.LocalOut1, 0x400)]
    [InlineData(PinSource.LocalOut2, 0x800)]
    [InlineData(PinSource.LocalOpen, 0x8000)]
    [InlineData(PinSource.On, 0x10000000)]
    public void EveryPinSourceUsesDriverEncoding(PinSource source, int value)
        => Assert.Equal(value, FastWindowsDevices.SettingsValues(new(cts: new(source)))["cts"]);

    [Fact]
    public void RenameWorksForDirectNamesAndPreservesStandardPortsBehavior()
    {
        var pairs = SetupOutputParser.ParsePairs("CNCA0 PortName=COM21\nCNCB0 PortName=COM#,RealPortName=COM22\n");
        var command = Com0ComCommand.ChangePort("CNCA0", new(realPortName: "COM31", emulateBaudRate: true));
        var adapted = WindowsCommandRunner.AdaptDirectPortRename(command, pairs);
        Assert.Equal("COM31", adapted.PortA!.PortName); Assert.Null(adapted.PortA.RealPortName);
        Assert.True(adapted.PortA.EmulateBaudRate);
        var standard = command with { PortId = "CNCB0" };
        Assert.Same(standard, WindowsCommandRunner.AdaptDirectPortRename(standard, pairs));
        var explicitClass = command with { PortA = new(portName: "COM#", realPortName: "COM31") };
        Assert.Same(explicitClass, WindowsCommandRunner.AdaptDirectPortRename(explicitClass, pairs));
    }

    [Fact]
    public void StandardClassOptInSurvivesBrokerSerialization()
    {
        var command = Com0ComCommand.CreateNamedPair("COM21", "COM22") with { UseStandardPortsClass = true };
        var json = System.Text.Json.JsonSerializer.Serialize(command);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<Com0ComCommand>(json)!.UseStandardPortsClass);
        Assert.False(Com0ComCommand.CreateNamedConnector("COM21").UseStandardPortsClass);
    }
}
