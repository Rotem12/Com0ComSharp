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

Download `Com0ComSharp.0.3.0.nupkg` from [Releases](https://github.com/Rotem12/Com0ComSharp/releases); the package is not on NuGet.org. Install it from the directory where you saved it:

```powershell
dotnet add package Com0ComSharp --version 0.3.0 --source C:\Downloads\Com0ComSharp
```

You also need the native com0com driver files. Use the [extraction script](scripts/Get-DriverPackage.ps1) or an existing installation. Place the matching release's `Com0ComSharp.Tool.exe` beside your app for automatic helper discovery. A .NET Framework 4.8 helper archive is also available.

## Create a port pair

```csharp
using Com0ComSharp;

var api = new Com0ComApi(@"C:\MyApp\com0com",
    new ClientOptions(allowLegacyDriver: true));

int id = api.CreatePair("COM21", "COM22");
// Use COM21 and COM22 with SerialPort.
api.DestroyDevice(id);
```

Async methods are available for UI apps. `CreateDevice("Pair", "21;22;0")` accepts basic VSPE Pair syntax; see the [migration guide](docs/vspe-migration.md). Ports persist until removed. Failures throw `Com0ComException` with diagnostic results. Advanced settings and batches remain available through `api.Client`.

`allowLegacyDriver` acknowledges the driver’s signing status without overriding Windows policy. C# 7.3 is supported.

## More

- [Driver setup and deployment](docs/deployment.md)
- [Windows driver verification](docs/driver-verification.md)
- [Framework sample and compatibility details](samples/Com0ComSharp.Framework)
- [Build and lifecycle sample](samples/Com0ComSharp.SmokeTest)
- [MIT license](LICENSE)
