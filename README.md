# Com0ComSharp

Manage com0com virtual serial ports from C# on Windows. Supports .NET Framework 4.6.2–4.8.1 and .NET 8–10.

Download the [NuGet package and matching helpers](https://github.com/Rotem12/Com0ComSharp/releases). Place `Com0ComSharp.Tool.exe` and `Com0ComSharp.Broker.exe` beside your app, and extract the native driver package with [`Get-DriverPackage.ps1`](scripts/Get-DriverPackage.ps1).

```csharp
using Com0ComSharp;

var api = new Com0ComApi(@"C:\MyApp\com0com",
    new ClientOptions(allowLegacyDriver: true));

int id = api.CreateDevice("21", emulateBaudRate: true); // First use installs driver + broker if needed; accepts "21", "COM21", or 21
api.DestroyDevice(id); // remove this pair
api.Stop();             // remove every pair; keep driver and broker installed
api.UninstallDriver();  // requests UAC and removes pairs, driver, and broker
```

`InstallDriver()` is optional; it checks and installs both components explicitly if you want to handle setup and reboot status before creating ports. The first device-management call does the same check automatically. Keep the matching `Com0ComSharp.Tool.exe` and `Com0ComSharp.Broker.exe` beside the app. Windows controls the elevation prompt and may request administrator credentials. Once installed, normal port operations use the broker without another prompt. `Stop()` removes all com0com pairs on the PC. Driver acceptance depends on Windows security policy. See the [driver compatibility notes](docs/driver-verification.md) and [VSPE migration guide](docs/vspe-migration.md).

The driver is legacy cross-signed and may be blocked by current Windows 11 policy. `allowLegacyDriver: true` acknowledges that limitation; it does not weaken or bypass Windows security settings. See the [Framework guide](docs/framework-compatibility.md) for .NET Framework deployment details.
