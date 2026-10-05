using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Com0ComSharp;
using Microsoft.Win32;

// Deliberate machine-modifying integration test, excluded from the unit-test suite.
if (args.Length != 5 || args[0] != "--package" || args[2] != "--report" || args[4] != "--allow-legacy")
{
    Console.Error.WriteLine("Run elevated: Com0ComSharp.SmokeTest.exe --package DIRECTORY --report REPORT.json --allow-legacy");
    return 2;
}
var reportPath = Path.GetFullPath(args[3]);
var steps = new List<object>();
var failure = (string?)null;
var cleanupFailed = false;
var staged = false;
var creationAttempted = false;
const int pairIndex = 812345;
var before = WindowsDiagnostics.InspectEnvironment();
var client = new Com0ComClient(DriverPackage.Open(args[1]), new() { Elevation = ElevationMode.RequireAdministrator, AllowLegacyDriver = true });
var baselineReservations = WindowsDiagnostics.GetReservedComPortNames().ToArray();
try
{
    if (!before.IsAdministrator) throw new InvalidOperationException("This test requires a deliberate elevated launch.");
    using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\com0com"))
        if (service is not null || before.Devices.Count != 0) throw new InvalidOperationException("Refusing lifecycle test: com0com is already in use or installed.");
    foreach (var inf in Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"), "oem*.inf"))
        if (Regex.IsMatch(File.ReadAllText(inf), @"^\s*CatalogFile(?:\.[\w.]+)?\s*=\s*com0com\.cat\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline))
            throw new InvalidOperationException("Refusing lifecycle test: com0com is already staged in the driver store.");
    var inspection = client.Package.Inspect();
    if (!inspection.IntegrityVerified) throw new InvalidDataException("Package integrity is unverified.");
    steps.Add(new { Step = "signatures", Inspection = inspection });
    var helper = Path.Combine(AppContext.BaseDirectory, "Com0ComSharp.Tool.exe");
    if (File.Exists(helper))
    {
        var brokerClient = new Com0ComClient(client.Package, new() { ElevationHelperPath = helper, AllowLegacyDriver = true });
        var help = await brokerClient.ExecuteAsync(new() { Operation = Com0ComOperation.Help });
        steps.Add(new { Step = "helper-ipc", Result = help }); help.ThrowIfFailed();
        if (!help.Output.Contains("Usage:", StringComparison.Ordinal)) throw new InvalidOperationException("Native help output did not return through the elevation helper.");
    }
    staged = true; // Also clean up a partially failed staging operation.
    var install = await client.InstallDriverAsync(); steps.Add(new { Step = "install", Result = install }); install.ThrowIfFailed();
    if (install.RebootRequired) throw new InvalidOperationException("Staging requires a reboot; no further changes are attempted.");
    creationAttempted = true;
    var create = await client.CreatePairAsync(new() { PortName = "COM#" }, new() { PortName = "COM#" }, pairIndex);
    steps.Add(new { Step = "create", Result = create }); create.ThrowIfFailed();
    if (create.RebootRequired) throw new InvalidOperationException("Creation requires a reboot; transfer is unverified.");
    var current = WindowsDiagnostics.InspectEnvironment(); steps.Add(new { Step = "device-readiness", Environment = current });
    var endpoints = current.Devices.Where(d => d.PortId is "CNCA812345" or "CNCB812345").ToArray();
    if (endpoints.Length != 2 || endpoints.Any(d => !d.Healthy)) throw new InvalidOperationException("Windows did not start both endpoints. Review device problem codes and Code Integrity events.");
    var pair = client.GetPairs().Single(p => p.Index == pairIndex);
    if (pair.A is null || pair.B is null) throw new InvalidOperationException("Pair discovery is incomplete.");
    Loopback(pair.A.EffectiveName, pair.B.EffectiveName);
    steps.Add(new { Step = "binary-transfer", A = pair.A.EffectiveName, B = pair.B.EffectiveName, BytesEachDirection = 512, Success = true });
    var reserved = new HashSet<string>(WindowsDiagnostics.GetReservedComPortNames(), StringComparer.OrdinalIgnoreCase);
    reserved.UnionWith(SerialPort.GetPortNames());
    var rename = Enumerable.Range(42, 200).Select(n => "COM" + n).First(n => !reserved.Contains(n));
    var change = await client.ChangePortAsync("CNCA812345", new() { RealPortName = rename, EmulateBaudRate = true });
    steps.Add(new { Step = "rename-and-emulation", Result = change }); change.ThrowIfFailed();
    if (change.RebootRequired) throw new InvalidOperationException("Configuration requires a reboot.");
    pair = client.GetPairs().Single(p => p.Index == pairIndex);
    if (pair.A!.EffectiveName != rename || !pair.A.Parameters.TryGetValue("EmuBR", out var emulation) || emulation != "yes")
        throw new InvalidOperationException("Configuration readback did not match the requested name/emulation.");
    Loopback(pair.A.EffectiveName, pair.B!.EffectiveName);
    steps.Add(new { Step = "transfer-after-change", Success = true, A = pair.A.EffectiveName, B = pair.B.EffectiveName });
}
catch (Exception e) { failure = e.ToString(); }
finally
{
    if (creationAttempted && WindowsDiagnostics.GetDevices().Count != 0)
    {
        try
        {
            var remove = await client.DestroyPairAsync(pairIndex); steps.Add(new { Step = "remove-pair", Result = remove });
            cleanupFailed |= !remove.Success || remove.RebootRequired;
        }
        catch (Exception e) { cleanupFailed = true; steps.Add(new { Step = "remove-pair", Error = e.ToString() }); }
    }
    if (staged)
    {
        try
        {
            var uninstall = await client.UninstallDriverAsync(); steps.Add(new { Step = "uninstall-driver", Result = uninstall });
            cleanupFailed |= !uninstall.Success || uninstall.RebootRequired;
        }
        catch (Exception e) { cleanupFailed = true; steps.Add(new { Step = "uninstall-driver", Error = e.ToString() }); }
    }
    var after = WindowsDiagnostics.InspectEnvironment();
    var reservations = WindowsDiagnostics.GetReservedComPortNames().ToArray();
    var restored = baselineReservations.SequenceEqual(reservations) && after.Devices.Count == 0 && before.SecureBootEnabled == after.SecureBootEnabled && before.MemoryIntegrityRunning == after.MemoryIntegrityRunning;
    if (staged && !restored) cleanupFailed = true;
    var report = new { Success = failure is null && !cleanupFailed, Failure = failure, CleanupFailed = cleanupFailed, Before = before, After = after, BaselineReservations = baselineReservations, FinalReservations = reservations, Steps = steps };
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
return failure is null && !cleanupFailed ? 0 : 1;

static void Loopback(string aName, string bName)
{
    using var a = Open(aName); using var b = Open(bName);
    var payload = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();
    a.Write(payload, 0, payload.Length); Verify(b, payload);
    Array.Reverse(payload); b.Write(payload, 0, payload.Length); Verify(a, payload);
    static SerialPort Open(string name)
    {
        var port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One) { ReadTimeout = 3000, WriteTimeout = 3000, DtrEnable = true, RtsEnable = true, Handshake = Handshake.None };
        try { port.Open(); return port; } catch { port.Dispose(); throw; }
    }
    static void Verify(SerialPort port, byte[] expected)
    {
        var received = new byte[expected.Length];
        var offset = 0;
        while (offset < received.Length) offset += port.Read(received, offset, received.Length - offset);
        if (!received.SequenceEqual(expected)) throw new InvalidDataException("Serial transfer bytes differ from the sent payload.");
    }
}
