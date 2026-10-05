# Moving from VSPE

`Com0ComApi` provides simple synchronous calls and corresponding `Async` methods. It works with .NET Framework 4.6.2–4.8.1 and modern .NET. The installed VSPE C# SDK and [Eterlogic's API documentation](https://eterlogic.com/help/vspe/VSPE_API_SharpPage.html) informed the naming. This is a Pair replacement, with a smaller set of behaviors than VSPE.

```csharp
var api = new Com0ComApi(@"C:\MyApp\com0com",
    new ClientOptions(allowLegacyDriver: true));

int id = api.CreatePair("COM21", "COM22");
// Or: int id = api.CreateDevice("Pair", "21;22;0");
var info = api.GetDeviceInfo(id);
api.SetBaudRateEmulation(id, true);
api.DestroyDevice(id);
```

For a UI application, use `await api.CreatePairAsync("COM21", "COM22")` and `await api.DestroyDeviceAsync(id)` to keep the UI responsive. The sample above also compiles with C# 7.3.

| VSPE call | Com0ComSharp equivalent |
| --- | --- |
| `vspe_createDevice("Pair", "21;22;0")` | `CreateDevice("Pair", "21;22;0")` or `CreatePair("COM21", "COM22")` |
| `vspe_destroyDevice(id)` | `DestroyDevice(id)` or `DestroyPair(id)` |
| `vspe_getDevicesCount()` | `GetDevicesCount()` |
| `vspe_getDeviceInfo(id, out ...)` | `GetDeviceInfo(id)` returns a `VirtualPortPair`, including endpoint names and settings. |
| `vspe_getDeviceIndexByComPortIndex(21)` | `GetDeviceIndexByComPortIndex(21)`, returning `-1` when absent. |
| Activation / initialization / start emulation | Open the API with a native package directory. Creation installs the driver as needed and starts the ports. |
| Driver deployment | `InstallDriver()` / `UninstallDriver()`, with async versions. Uninstall removes every com0com pair on this PC. |

## Behavior to account for

- `CreateDevice` accepts only the basic Pair string `COM-number;COM-number;baud-emulation`, with the final field `0` or `1`, as [documented by Eterlogic](https://eterlogic.com/help/vspe/VSPE_API_DevicesPage.html). Extended VSPE strings and other device types throw before creating anything. Connector, Splitter, TCP/UDP, multiple lanes, and VSPE configuration files are unsupported.
- Successful creation returns the stable com0com pair ID. IDs can be sparse. Enumerate `GetDevices()` rather than assuming IDs run from zero to `GetDevicesCount() - 1`. Discovery covers every com0com pair on the PC; save the ID of each pair your application creates.
- Changes throw `Com0ComException` on failure instead of VSPE's `0` / `-1` failure values. Inspect `error.Result.Failure`, `Output`, `RebootRequired`, and `CreatedPairIndex`. A partial pair can remain after failure or cancellation; inspect it before cleanup or retrying. There is no automatic rollback.
- Ports remain installed after your process exits. There is no activation key, release step, or global emulation loop. Explicitly destroy app-owned pairs when appropriate. `StartEmulation`, `StopEmulation`, and global device destruction are not emulated. Advanced native global enable/disable commands are available through `Client` and affect all com0com ports.
- Simple creation accepts free/unreserved standard Windows names from `COM1` through `COM4096`. It uses the actual native allocated pair ID, converts each endpoint to the standard Ports class, verifies the requested names and checks that both Windows devices are healthy. Another application's COM reservations are never released. Perform your application's serial handshake after creation.

## Elevation and configuration

Place the **matching release's** helper beside your application, or pass `new ClientOptions(elevationHelperPath: @"C:\MyApp\Com0ComSharp.Tool.exe", allowLegacyDriver: true)`. The facade discovers the adjacent helper automatically. The Framework helper requires all files from its ZIP; the modern x64 helper is self-contained. Named creation performs its adaptive steps inside one helper invocation, with at most one UAC request per call. UAC cannot be bypassed; an account without permission needs IT provisioning. Already elevated installers can execute directly without a helper.

`InstallDriver` and other successful native changes can return `RebootRequired=true`; check before continuing. Named creation stops and throws with `FailureKind.RebootRequired` if Windows needs a restart before configuration completes. The library never restarts Windows. It preserves legacy signature opt-in and Windows driver policy checks.

For uncommon options use `api.ChangePort("CNCA0", new PortSettings(emulateOverrun: true))`, or the detailed `api.Client` API. Specify the endpoint ID from `GetDeviceInfo(id).A.Id` / `.B.Id`. The earlier typed API remains available.

The adaptive sequence and facade are covered by automated tests on every target, including mocked device failures, naming collisions, cancellation and partial results. The previous native lifecycle test remains documented in the [driver report](driver-verification.md); this facade's complete elevated sequence and restricted-account UAC flow await a live test.
