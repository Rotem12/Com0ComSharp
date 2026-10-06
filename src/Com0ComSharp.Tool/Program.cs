using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Com0ComSharp;

if (args.Length == 3 && args[0] == "--broker") return await ElevationBroker.RunElevatedAsync(args[1], args[2]);
var json = new JsonSerializerOptions { WriteIndented = true };
if (args.Length == 0 || args[0] is "--help" or "help")
{
    Console.WriteLine("""
        Com0ComSharp.Tool
          diagnose [--package DIRECTORY]
          list
          download --out INSTALLER.exe
          install-driver --package DIRECTORY --allow-legacy
          create --package DIRECTORY --a COM21 [--b COM22] --allow-legacy
          destroy --package DIRECTORY --index N
          change --package DIRECTORY --port CNCA0 --settings-file SETTINGS.json
          stop --package DIRECTORY
          uninstall-driver --package DIRECTORY
          native-help --package DIRECTORY
        First-time setup requests administrator approval. Normal port operations use the installed broker.
        stop removes all com0com pairs; uninstall-driver also removes the driver and broker.
        Add --require-admin to disable elevation prompts.
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
    string? Optional(string key) => options.TryGetValue(key, out var value) ? value : null;
    if (args[0] == "download") { Console.WriteLine(await DriverDownload.DownloadInstallerAsync(Required("--out"))); return 0; }
    if (args[0] == "diagnose")
    {
        var package = Optional("--package") is string path ? DriverPackage.Open(path) : WindowsDiagnostics.FindInstalledPackage();
        var version = typeof(Com0ComApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var status = WindowsServiceStatus.Query();
        var readiness = package is null ? null : await new ManagementBrokerClient(package, new ClientOptions(timeout: TimeSpan.FromSeconds(5))).ProbeAsync(default);
        Console.WriteLine(JsonSerializer.Serialize(new { LibraryVersion = version, BrokerService = status, Readiness = readiness,
            Environment = WindowsDiagnostics.InspectEnvironment(), Package = package?.DirectoryPath, Signing = package?.Inspect() }, json));
        return 0;
    }
    if (args[0] == "list") { Console.WriteLine(JsonSerializer.Serialize(WindowsDiagnostics.GetPairs(), json)); return 0; }
    var native = Optional("--package") is string specified ? DriverPackage.Open(specified)
        : WindowsDiagnostics.FindInstalledPackage() ?? throw new ArgumentException("Specify --package with the extracted driver directory.");
    var api = new Com0ComApi(native.DirectoryPath, new ClientOptions(
        elevation: options.ContainsKey("--require-admin") ? ElevationMode.RequireAdministrator : ElevationMode.Prompt,
        allowLegacyDriver: options.ContainsKey("--allow-legacy")));
    CommandResult result;
    switch (args[0])
    {
        case "install-driver": result = await api.InstallDriverAsync(); break;
        case "create":
        case "provision":
            if (Optional("--index") is not null) throw new ArgumentException("Creation returns its allocated pair ID. --index is for destruction.");
            var id = Optional("--b") is string b
                ? await api.CreatePairAsync(Required("--a"), b)
                : await api.CreateDeviceAsync(Required("--a"));
            Console.WriteLine(JsonSerializer.Serialize(new { CreatedPairIndex = id, Device = api.GetDeviceInfo(id) }, json));
            return 0;
        case "destroy": result = await api.DestroyDeviceAsync(int.Parse(Required("--index"), CultureInfo.InvariantCulture)); break;
        case "change": result = await api.ChangePortAsync(Required("--port"), JsonSerializer.Deserialize<PortSettings>(File.ReadAllText(Required("--settings-file"))) ?? throw new ArgumentException("Invalid settings JSON.")); break;
        case "stop": result = await api.StopAsync(); break;
        case "uninstall-driver": result = await api.UninstallDriverAsync(); break;
        case "native-help": result = await new Com0ComClient(native, new ClientOptions(ElevationMode.RequireAdministrator)).ExecuteAsync(new(Com0ComOperation.Help)); break;
        default: throw new ArgumentException("Unknown command: " + args[0]);
    }
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return result.Success ? (result.RebootRequired ? 2 : 0) : 1;
}
catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or InvalidDataException or System.ComponentModel.Win32Exception or JsonException or Com0ComException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
