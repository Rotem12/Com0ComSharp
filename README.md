# Com0ComSharp

Manage com0com virtual serial port pairs from C# on Windows.

**Targets:** .NET Framework 4.6.2–4.8.1, .NET 8/9/10 · **Architectures:** x86/x64

[Download the package and helpers](https://github.com/Rotem12/Com0ComSharp/releases) · [Framework guide](docs/framework-compatibility.md) · [Driver details](docs/driver-verification.md)

## Features

- Install or remove the com0com driver and create, list, configure, or remove port pairs.
- Configure port names, baud-rate and buffer emulation, and serial signal mapping.
- Read device and driver diagnostics. The companion helper can handle one UAC prompt for a batch of changes.
- Use the ports with `System.IO.Ports.SerialPort`.

Driver installation/removal requires administrator approval; uninstalling removes all com0com pairs on the machine. The upstream driver is legacy cross-signed, and Windows security policy may block it. See the [compatibility report](docs/driver-verification.md).

## Install

Download `Com0ComSharp.0.2.0.nupkg` from [Releases](https://github.com/Rotem12/Com0ComSharp/releases); the package is not on NuGet.org. Install it from the directory where you saved it:

```powershell
dotnet add package Com0ComSharp --version 0.2.0 --source C:\Downloads\Com0ComSharp
```

You also need the native com0com driver files; they are not included in the release. Use the [driver extraction script](scripts/Get-DriverPackage.ps1) or an existing installation. The release includes a self-contained helper and a .NET Framework 4.8 helper archive.

## Create a port pair

```csharp
using Com0ComSharp;

var package = DriverPackage.Open(@"C:\MyApp\com0com");
var client = new Com0ComClient(package, new ClientOptions(
    elevation: ElevationMode.Prompt,
    elevationHelperPath: @"C:\MyApp\Com0ComSharp.Tool.exe",
    allowLegacyDriver: true));

var result = await client.ExecuteBatchAsync(new[]
{
    Com0ComCommand.InstallDriver(),
    Com0ComCommand.CreatePair(
        new PortSettings(portName: "COM#"),
        new PortSettings(portName: "COM#"),
        pairIndex: 25)
});
result.ThrowIfFailed();
```

`allowLegacyDriver` acknowledges the supplied driver’s signing status; it does not override Windows policy. The API targets C# 7.3 projects as well as newer C# versions.

## More

- [Driver setup and deployment](docs/deployment.md)
- [Windows driver verification](docs/driver-verification.md)
- [Framework sample and compatibility details](samples/Com0ComSharp.Framework)
- [Build and lifecycle sample](samples/Com0ComSharp.SmokeTest)
- [MIT license](LICENSE)
