using System.Runtime.InteropServices;
using Com0ComSharp;
using Xunit;

namespace Com0ComSharp.Tests;

public sealed class ProcessTests : IDisposable
{
    private readonly string directory = RuntimeCompatibility.CreateTemporaryDirectory("com0com-tests-");
    private DriverPackage CreatePackage()
    {
        foreach (var file in DriverPackage.RequiredFiles) File.WriteAllBytes(Path.Combine(directory, file), PeBytes());
        return DriverPackage.Open(directory);
    }
    private static byte[] PeBytes()
    {
        var data = new byte[128]; data[0] = 0x4d; data[1] = 0x5a; data[60] = 64;
        data[64] = 0x50; data[65] = 0x45;
        var machine = RuntimeInformation.OSArchitecture == Architecture.X64 ? (ushort)0x8664 : (ushort)0x014c;
        data[68] = (byte)(machine & 0xff); data[69] = (byte)(machine >> 8);
        return data;
    }

    [Fact]
    public void ElevatedRunUsesRunasAndWhitespaceFreeNativeLogPath()
    {
        var start = WindowsCommandRunner.BuildStartInfo(CreatePackage(), new[] { "--silent", "list" }, @"C:\Temp\setup.log", true);
        Assert.Equal("runas", start.Verb); Assert.True(start.UseShellExecute);
        Assert.False(start.RedirectStandardOutput); Assert.Equal(@"C:\Temp\setup.log", GetArguments(start)[1]);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
    }

    [Fact]
    public void IndexedDeferredInstallFitsRealEightSlotNativeParser()
    {
        var command = Com0ComCommand.CreatePair(new() { PortName = "COM#" }, new() { PortName = "COM#" }, 812345, true);
        var start = WindowsCommandRunner.BuildStartInfo(CreatePackage(), command.ToArguments(), @"C:\Temp\setup.log", true);
        var arguments = GetArguments(start);
        Assert.Equal(8, arguments.Count);
        Assert.Equal("PortName=COM#", arguments[arguments.Count - 1]);
    }

    [Fact]
    public void HelperCapturesStdoutWithoutPassingWhitespaceLogPathToNativeParser()
    {
        var start = WindowsCommandRunner.BuildStartInfo(CreatePackage(), Com0ComCommand.CreatePair(pairIndex: 1).ToArguments(), @"C:\Users\Test User\setup.log", false);
        Assert.DoesNotContain("--output", GetArguments(start)); Assert.True(start.RedirectStandardOutput);
    }

    [Fact]
    public void RejectsIncompletePackage() => Assert.Throws<FileNotFoundException>(() => DriverPackage.Open(directory));

    [Fact]
    public void RejectsMixedArchitectures()
    {
        CreatePackage();
        var bytes = PeBytes(); bytes[68] = 0x64; bytes[69] = 0xaa; // ARM64
        File.WriteAllBytes(Path.Combine(directory, "setup.dll"), bytes);
        Assert.Throws<PlatformNotSupportedException>(() => DriverPackage.Open(directory));
    }

    [Fact]
    public async Task BatchStopsAtFirstFailureWithoutLosingFirstResult()
    {
        var runner = new FakeRunner();
        var client = new Com0ComClient(CreatePackage(), commandRunner: runner);
        var result = await client.ExecuteBatchAsync([Com0ComCommand.RemovePair(1), Com0ComCommand.RemovePair(2), Com0ComCommand.RemovePair(3)]);
        Assert.False(result.Success); Assert.Equal(2, runner.Count); Assert.Equal(2, result.Results.Count);
    }

    [Fact]
    public async Task WholeBatchIsValidatedBeforeAnyMutation()
    {
        var runner = new FakeRunner();
        var client = new Com0ComClient(CreatePackage(), commandRunner: runner);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ExecuteBatchAsync([Com0ComCommand.RemovePair(1), Com0ComCommand.RemovePair(-1)]));
        Assert.Equal(0, runner.Count);
    }

    [Fact]
    public async Task AlreadyCancelledBatchDoesNotStartNativeProcess()
    {
        var runner = new FakeRunner();
        var client = new Com0ComClient(CreatePackage(), commandRunner: runner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteBatchAsync([Com0ComCommand.RemovePair(1)], new CancellationToken(true)));
        Assert.Equal(0, runner.Count);
    }

    private sealed class FakeRunner : ICom0ComCommandRunner
    {
        public int Count { get; private set; }
        public Task<CommandResult> RunAsync(DriverPackage package, Com0ComCommand command, ClientOptions options, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.FromResult(new CommandResult(command.Operation, Count == 1 ? 0 : 1, "fixture", Count == 1 ? FailureKind.None : FailureKind.ProcessFailed));
        }
    }
    private static IReadOnlyList<string> GetArguments(System.Diagnostics.ProcessStartInfo start)
    {
#if NETFRAMEWORK
        // These setupc fixtures deliberately contain no whitespace; more complex
        // quoting is checked against Windows' own parser in CompatibilityTests.
        return start.Arguments.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
#else
        return start.ArgumentList.ToArray();
#endif
    }
    public void Dispose() => Directory.Delete(directory, true);
}
