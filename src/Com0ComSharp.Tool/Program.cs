using System.Globalization;
using System.Text.Json;
using Com0ComSharp;

if (args.Length == 3 && args[0] == "--broker") return await ElevationBroker.RunElevatedAsync(args[1], args[2]);
var json = new JsonSerializerOptions { WriteIndented = true };
if (args.Length == 0 || args[0] is "--help" or "help")
{
    Console.WriteLine("""
        Com0ComSharp.Tool
          diagnose [--package DIRECTORY]         Read-only environment and signing report
          list                                   Read-only pair discovery
          download --out INSTALLER.exe            Download pinned installer; no execution
          install-driver --package DIRECTORY [--allow-legacy]
          create --package DIRECTORY [--a COM#] [--b COM#] [--index N] [--allow-legacy]
          provision --package DIRECTORY [--index N] [--a COM#] [--b COM#] [--allow-legacy]
          destroy --package DIRECTORY --index N
          change --package DIRECTORY --port CNCA0 --settings-file SETTINGS.json
          uninstall-driver --package DIRECTORY --all-pairs
          native-help --package DIRECTORY
        Add --require-admin to refuse elevation; otherwise Windows UAC is requested when needed.
        Use the published .exe for a single UAC prompt per batch. Running via dotnet uses per-command elevation.
        --allow-legacy acknowledges compatibility risk; it does not change Windows security settings.
        """);
    return 0;
}
try
{
    var flags = new HashSet<string> { "--allow-legacy", "--require-admin", "--all-pairs" };
    var valued = new HashSet<string> { "--package", "--out", "--a", "--b", "--index", "--port", "--settings-file" };
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i++)
    {
        var key = args[i];
        if (options.ContainsKey(key)) throw new ArgumentException("Duplicate option: " + key);
        if (flags.Contains(key)) options.Add(key, "true");
        else if (valued.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) options.Add(key, args[++i]);
        else throw new ArgumentException("Unknown option or missing value: " + key);
    }
    string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Missing " + key);
    string? Optional(string key) => options.GetValueOrDefault(key);
    if (args[0] == "download") { Console.WriteLine(await DriverDownload.DownloadInstallerAsync(Required("--out"))); return 0; }
    if (args[0] == "diagnose")
    {
        var package = Optional("--package") is string path ? DriverPackage.Open(path) : WindowsDiagnostics.FindInstalledPackage();
        Console.WriteLine(JsonSerializer.Serialize(new { Environment = WindowsDiagnostics.InspectEnvironment(), Package = package?.DirectoryPath, Signing = package?.Inspect(), ReservedComPorts = WindowsDiagnostics.GetReservedComPortNames() }, json));
        return 0;
    }
    if (args[0] == "list") { Console.WriteLine(JsonSerializer.Serialize(WindowsDiagnostics.GetPairs(), json)); return 0; }
    var native = Optional("--package") is string specified ? DriverPackage.Open(specified) : WindowsDiagnostics.FindInstalledPackage() ?? throw new ArgumentException("Specify --package with the complete extracted package directory.");
    var index = Optional("--index") is string number ? int.Parse(number, CultureInfo.InvariantCulture) : (int?)null;
    var self = Environment.ProcessPath;
    var helper = self is not null && !Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : null;
    var client = new Com0ComClient(native, new()
    {
        AllowLegacyDriver = options.ContainsKey("--allow-legacy"), ElevationHelperPath = helper,
        Elevation = options.ContainsKey("--require-admin") ? ElevationMode.RequireAdministrator : ElevationMode.Prompt
    });
    Com0ComCommand[] plan = args[0] switch
    {
        "install-driver" => [Com0ComCommand.InstallDriver()],
        "create" => [Com0ComCommand.CreatePair(new() { PortName = Optional("--a") ?? "COM#" }, new() { PortName = Optional("--b") ?? "COM#" }, index)],
        "provision" => [Com0ComCommand.InstallDriver(), Com0ComCommand.CreatePair(new() { PortName = Optional("--a") ?? "COM#" }, new() { PortName = Optional("--b") ?? "COM#" }, index)],
        "destroy" => [Com0ComCommand.RemovePair(index ?? throw new ArgumentException("--index is required."))],
        "change" => [Com0ComCommand.ChangePort(Required("--port"), JsonSerializer.Deserialize<PortSettings>(File.ReadAllText(Required("--settings-file"))) ?? throw new ArgumentException("Invalid settings JSON."))],
        "uninstall-driver" when options.ContainsKey("--all-pairs") => [new() { Operation = Com0ComOperation.UninstallDriver }],
        "uninstall-driver" => throw new ArgumentException("Uninstall removes every com0com pair system-wide. Supply --all-pairs to acknowledge this scope."),
        "native-help" => [new() { Operation = Com0ComOperation.Help }],
        _ => throw new ArgumentException("Unknown command: " + args[0])
    };
    var result = await client.ExecuteBatchAsync(plan);
    var environment = WindowsDiagnostics.InspectEnvironment();
    Console.WriteLine(JsonSerializer.Serialize(new { Batch = result, Environment = environment, Pairs = client.GetPairs() }, json));
    if (!result.Success) return 1;
    // A successful setupc exit is not sufficient evidence of functional ports.
    if (args[0] is "create" or "provision")
    {
        var installed = SetupOutputParser.ParsePairs(result.Results.Last().Output);
        var pair = index ?? (installed.Count == 1 ? installed[0].Index : (int?)null);
        var endpoints = environment.Devices.Where(d => pair.HasValue && (d.PortId == "CNCA" + pair || d.PortId == "CNCB" + pair)).ToArray();
        if (result.RebootRequired || !pair.HasValue || endpoints.Length != 2 || endpoints.Any(d => !d.Healthy))
        {
            Console.Error.WriteLine("Pair readiness is unverified or blocked. Review device ProblemCode and Code Integrity events before using the ports.");
            return 2;
        }
    }
    return 0;
}
catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception or JsonException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
