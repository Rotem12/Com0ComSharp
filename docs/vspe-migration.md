# VSPE-style usage

```csharp
var api = new Com0ComApi(nativeDriverDirectory,
    new ClientOptions(allowLegacyDriver: true));

int connector = api.CreateDevice("Connector", "21;1");
int pair = api.CreateDevice("Pair", "22;23;0");
api.SetBaudRateEmulation(pair, true);
api.DestroyDevice(connector);
api.Stop();
```

| Action | Call |
| --- | --- |
| One visible COM port | `CreateDevice("COM21", true)` or `CreateDevice(21, true)` |
| Two connected COM ports | `CreatePair("COM21", "COM22")` |
| List devices | `GetDevices()` / `GetDevicesCount()` |
| Device details | `GetDeviceInfo(id)` |
| Find a device by COM number | `GetDeviceIndexByComPortIndex(21)` |
| Configure an endpoint | `ChangePort("CNCA0", new PortSettings(emulateOverrun: true))` |
| Remove one device | `DestroyDevice(id)` |
| Remove all com0com pairs on the PC | `Stop()` |

Ports are active when created and persist until destroyed. Save the returned ID; IDs need not be consecutive. The Connector uses a hidden paired endpoint and does not reproduce VSPE's multi-client behavior. Splitter and network devices are unsupported.

The baud flag enables emulation; set the actual speed in `SerialPort`. Methods also have `Async` versions for UI applications. Failures throw `Com0ComException`; `Result` includes the failure, restart status, and any partially created pair ID.

Use `Com0ComApi` for normal port management. Its advanced `Client` exposes native driver commands that may require elevation.
