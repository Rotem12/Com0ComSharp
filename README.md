# Com0ComSharp

Manage com0com virtual serial ports from C# on Windows. Supports .NET Framework 4.6.2–4.8.1 and .NET 8–10.

Download the [NuGet package and helpers](https://github.com/Rotem12/Com0ComSharp/releases/latest). Extract `Com0ComSharp.Helpers.win-x64.zip` beside your app. Get the native driver files with [`Get-DriverPackage.ps1`](scripts/Get-DriverPackage.ps1).

```csharp
using Com0ComSharp;

var api = new Com0ComApi(@"C:\MyApp\com0com",
    new ClientOptions(allowLegacyDriver: true));

int id = api.CreateDevice("COM21", emulateBaudRate: true);
api.DestroyDevice(id);
api.Stop(); // Removes all com0com pairs on this PC.
```

First use installs the driver and automatic-start broker with one Windows administrator prompt. Later create, destroy, configure, and stop calls need no elevation. `InstallDriver()` is optional; its result reports whether a restart is needed. `UninstallDriver()` removes both components with elevation.

Port names accept `"COM21"`, `"21"`, or `21`. Use `CreatePair("COM21", "COM22")` for two connected ports. Set the actual baud rate when opening `SerialPort`.

The legacy signed driver can be blocked by Windows 11 security policy. [Setup](docs/deployment.md) · [VSPE usage](docs/vspe-migration.md) · [.NET Framework](docs/framework-compatibility.md)
