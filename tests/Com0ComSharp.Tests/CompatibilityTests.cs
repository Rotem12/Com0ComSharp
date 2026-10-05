using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Com0ComSharp.Tests;

public sealed class CompatibilityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData(@"C:\Program Files\My App\")]
    [InlineData("embedded\"quote")]
    [InlineData("backslash\\\"quote")]
    [InlineData("tab\tand space")]
    [InlineData("תיקייה עם רווח")]
    public void ArgumentsRoundTripThroughWindowsParser(string argument)
    {
        var expected = new[] { "--broker", argument, "last" };
        var start = new ProcessStartInfo();
        RuntimeCompatibility.SetArguments(start, expected);
#if NETFRAMEWORK
        var commandLine = start.Arguments;
#else
        var commandLine = string.Join(" ", start.ArgumentList.Select(RuntimeCompatibility.QuoteArgument));
#endif
        var pointer = CommandLineToArgvW("fixture.exe " + commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            var actual = Enumerable.Range(1, count - 1)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))).ToArray();
            Assert.Equal(expected, actual);
        }
        finally { LocalFree(pointer); }
    }

    [Fact]
    public async Task ProcessWaitHandlesAlreadyExitedChildAndPreservesExitCode()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 7")
        { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await RuntimeCompatibility.WaitForExitAsync(process, timeout.Token);
        Assert.Equal(7, process.ExitCode);
    }

    [Fact]
    public async Task CancellingProcessWaitDoesNotClaimProcessWasStopped()
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var wait = RuntimeCompatibility.WaitForExitAsync(process, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) RuntimeCompatibility.KillProcess(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RuntimeCompatibility.WaitForExitAsync(process, timeout.Token);
        }
    }

    [Fact]
    public async Task ProtectedPipeReturnsJsonAndCancelsReadWithoutClosingConnection()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Keep the test's real transport bounded, without UAC or native setupc.
        var name = "Com0ComSharp-test-" + Guid.NewGuid().ToString("N");
        using var server = RuntimeCompatibility.CreateResultPipe(name, acl);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(5000, timeout.Token);
        await connection;
        using var stop = new CancellationTokenSource();
        var read = RuntimeCompatibility.WaitAsync(client.ReadAsync(new byte[1], 0, 1, stop.Token), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        var result = new BatchResult(new[] { new CommandResult(Com0ComOperation.Help, 0, "תוצאה", FailureKind.None) }, 1);
        using (var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(result));
            await writer.FlushAsync();
        }
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
        var line = await RuntimeCompatibility.WaitAsync(reader.ReadLineAsync(), timeout.Token);
        var response = JsonSerializer.Deserialize<BatchResult>(line!);
        Assert.True(response!.Success);
        Assert.Equal("תוצאה", Assert.Single(response.Results).Output);
    }

    [Fact]
    public void CommandJsonKeepsInitPropertiesPinMappingAndExplicitOperation()
    {
        var command = Com0ComCommand.ChangePort("CNCA25", new PortSettings
        { RealPortName = "COM42", EmulateBaudRate = true, Cts = new PinMapping(PinSource.LocalDtr, true) });
        var json = JsonSerializer.Serialize(command);
        var restored = JsonSerializer.Deserialize<Com0ComCommand>(json)!;
        Assert.Equal(command.ToArguments(), restored.ToArguments());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Com0ComCommand>("{}"));
    }

    [Fact]
    public void ConstructorBasedApiMatchesInitConfiguration()
    {
        var settings = new PortSettings(realPortName: "COM42", emulateBaudRate: true,
            cts: new PinMapping(PinSource.LocalDtr, true));
        var command = new Com0ComCommand(Com0ComOperation.ChangePort, portId: "CNCA25", portA: settings);
        var expected = Com0ComCommand.ChangePort("CNCA25", new PortSettings
        { RealPortName = "COM42", EmulateBaudRate = true, Cts = new PinMapping(PinSource.LocalDtr, true) });
        Assert.Equal(expected.ToArguments(), command.ToArguments());
        var options = new ClientOptions(ElevationMode.RequireAdministrator, elevationHelperPath: "helper.exe", allowLegacyDriver: true);
        Assert.Equal(TimeSpan.FromMinutes(3), options.Timeout);
        Assert.Equal(ElevationMode.RequireAdministrator, options.Elevation);
        Assert.Equal("helper.exe", options.ElevationHelperPath);
        Assert.True(options.AllowLegacyDriver);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
