# Moving from VSPE

`Com0ComApi` provides simple synchronous calls and corresponding `Async` methods. It works with .NET Framework 4.6.2–4.8.1 and modern .NET. The installed VSPE C# SDK and [Eterlogic's API documentation](https://eterlogic.com/help/vspe/VSPE_API_SharpPage.html) informed the naming. It provides VSPE-style Connector and Pair calls, with a smaller set of behaviors than VSPE.

```csharp
var api = new Com0ComApi(@"C:\MyApp\com0com",
    new ClientOptions(allowLegacyDriver: true));

int id = api.CreatePair("COM21", "COM22");
// Or: int id = api.CreateDevice("Pair", "21;22;0");
var info = api.GetDeviceInfo(id);
api.SetBaudRateEmulation(id, true);
int connectorId = api.CreateDevice("COM23", emulateBaudRate: true);
// Integer and plain-number inputs work too: api.CreateDevice(22, true) or api.CreateDevice("22", true)
api.DestroyDevice(id); // removes this pair only
await api.StopAsync(); // removes every pair; leaves the driver installed
```

For a UI application, use `await api.CreatePairAsync("COM21", "COM22")` and `await api.DestroyDeviceAsync(id)` to keep the UI responsive. The sample above also compiles with C# 7.3.

| VSPE call | Com0ComSharp equivalent |
| --- | --- |
| `vspe_createDevice("Pair", "21;22;0")` | `CreateDevice("Pair", "21;22;0")` or `CreatePair("COM21", "COM22")` |
| `vspe_createDevice("Connector", "21;1")` | `CreateDevice("COM21", true)` / `CreateDevice(21, true)` or `CreateDevice("Connector", "21;1")` |
| `vspe_destroyDevice(id)` | `DestroyDevice(id)` or `DestroyPair(id)` |
| `vspe_getDevicesCount()` | `GetDevicesCount()` |
| `vspe_getDeviceInfo(id, out ...)` | `GetDeviceInfo(id)` returns a `VirtualPortPair`, including endpoint names and settings. |
| `vspe_getDeviceIndexByComPortIndex(21)` | `GetDeviceIndexByComPortIndex(21)`, returning `-1` when absent. |
| `vspe_destroyAllDevices(); vspe_stopEmulation(); vspe_release()` | `Stop()` / `StopAsync()`. Destroys every com0com pair system-wide; the driver remains installed. |
| Activation / initialization / start emulation | Open the API with a native package directory. Creation installs the driver as needed and starts the ports. |
| Driver deployment | `InstallDriver()` / `UninstallDriver()`, with async versions. Uninstall removes every com0com pair on this PC. |

## Behavior to account for

- `CreateDevice` accepts the basic Pair string `COM-number;COM-number;baud-emulation` or Connector string `COM-number;baud-emulation`, with the final field `0` or `1`, as [documented by Eterlogic](https://eterlogic.com/help/vspe/VSPE_API_DevicesPage.html). Extended VSPE strings, Splitter and network devices, multiple lanes, and VSPE configuration files are unsupported.
- `CreateDevice("COM21", true)` makes one visible standard COM port with an internal hidden paired endpoint. The name may be `"21"`, `"COM21"`, or integer `21`. The bool controls baud-rate emulation; it does not set a fixed serial speed. Set that when your app opens `SerialPort`.
- Successful creation returns the stable com0com pair ID. IDs can be sparse. Enumerate `GetDevices()` rather than assuming IDs run from zero to `GetDevicesCount() - 1`. Discovery covers every com0com pair on the PC; save the ID of each pair your application creates.
- Changes throw `Com0ComException` on failure instead of VSPE's `0` / `-1` failure values. Inspect `error.Result.Failure`, `Output`, `RebootRequired`, and `CreatedPairIndex`. A partial pair can remain after failure or cancellation; inspect it before cleanup or retrying. There is no automatic rollback.
- Ports remain installed after your process exits. There is no activation key, release step, or global emulation loop. Explicitly destroy app-owned pairs when appropriate. `StartEmulation` and `StopEmulation` are not emulated; `Stop()` removes all pairs through the installed broker. Advanced native global enable/disable commands are available through `Client` and affect all com0com ports.
- Simple creation accepts free/unreserved standard Windows names from `COM1` through `COM4096`. It uses the actual native allocated pair ID, converts each endpoint to the standard Ports class, verifies the requested names and checks that both Windows devices are healthy. Another application's COM reservations are never released. Perform your application's serial handshake after creation.

## Installation and elevation

Place the complete native driver package and the matching release's `Com0ComSharp.Tool.exe` and `Com0ComSharp.Broker.exe` beside your app. The API detects missing components, requests Windows elevation once, then installs the driver and broker service together:

```csharp
var api = new Com0ComApi(nativeDriverDirectory,
    new ClientOptions(allowLegacyDriver: true));

var setup = api.InstallDriver();
if (setup.RebootRequired) Console.WriteLine("Restart Windows before creating ports.");

int id = api.CreateDevice("21", emulateBaudRate: true);
api.DestroyDevice(id);
api.Stop();            // Removes every pair, keeps driver and broker.
api.UninstallDriver(); // Removes pairs, driver, and broker with elevation.
```

`InstallDriver()` checks for both the registered driver and a responding broker service. It is idempotent when both are ready. The helper provides the UAC prompt; Windows may ask for consent or administrator credentials according to local policy. Once installed, the LocalSystem broker accepts allowlisted pair create/remove commands from local users, so normal device changes and `Stop()` need no elevation. `Stop()` has machine-wide effect. The library never restarts Windows.

For managed deployments that provision software outside the application, the PowerShell setup and uninstall scripts remain available. The .NET Framework helper archive must be deployed with all its files; the modern x64 helper and broker are self-contained.

For uncommon options use `api.ChangePort("CNCA0", new PortSettings(emulateOverrun: true))`, or the detailed `api.Client` API. Specify the endpoint ID from `GetDeviceInfo(id).A.Id` / `.B.Id`. The earlier typed API remains available.

The broker service installation, standard-user pipe operations, and live device changes have not been exercised on this PC; use a controlled Windows test machine before production deployment.
