# Com0ComSharp

C# API for installing/managing the Windows com0com driver and its virtual serial port pairs, with UAC elevation and read-only diagnostics. Targets **.NET 8 and .NET 10**, Windows x64/x86. ARM64 is unsupported by the supplied upstream package.

**Windows 11 compatibility is conditional.** The [vovsoft 3.0.0 package](https://github.com/vovsoft/com0com) contains a valid legacy cross-signed driver. It is not a verified modern WHCP release. Current Windows policy can block it even when its signature verifies. Read the [driver verification report](docs/driver-verification.md) before deployment on restricted PCs.

**Live-tested on Windows 11 x64 build 26200 with Memory Integrity running and Secure Boot off:** driver install, COM pair creation, binary transfer in both directions, rename/baud emulation, transfer after configuration, pair destruction, and driver uninstall all passed. All 36 tests also pass on each .NET target. This does not certify other Windows security configurations.

## Capabilities

- Stage/uninstall/update/reload the driver; create/destroy/list pairs and change endpoint settings.
- Automatic standard COM-port allocation (`COM#`), custom names, and changing allocated COM numbers.
- Baud-rate and buffer-overrun emulation; noise injection; additional read timeouts.
- Plug-in, exclusive, hidden, and all-data-bits modes; CTS/DSR/DCD/RI pin mapping and inversion.
- Enable/disable all pairs, clean old INF packages, enumerate busy names, and update/list friendly names through typed commands.
- One UAC prompt for a batch using the companion helper; explicit outcomes for refusal, missing privileges, failures, cancellation, and timeout.
- Read-only native device status, running Memory Integrity/Secure Boot state, reserved COM names, and signature/catalog-membership inspection.
- SHA-256-pinned installer download. Downloading does not execute it.

The base driver provides paired ports. VSPE splitters, mergers, TCP bridges, and remote serial features are not implemented here. Upstream hub4com is a separate project/tool. Serial I/O uses standard `System.IO.Ports.SerialPort` once ports are running.

## Build and package

```powershell
dotnet build Com0ComSharp.sln -c Release
dotnet test tests/Com0ComSharp.Tests -c Release --no-build
dotnet pack src/Com0ComSharp -c Release --no-build -o .artifacts/packages
dotnet publish src/Com0ComSharp.Tool -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .artifacts/tool
```

Reference `src/Com0ComSharp/Com0ComSharp.csproj` or install the `.nupkg` from a local feed/GitHub release. Version 0.1.0 is not published to NuGet.org. CI builds/tests managed code; it does not install kernel drivers on hosted runners.

## Obtain the native package

Use an existing com0com installation, or download/extract the pinned installer with [Get-DriverPackage.ps1](scripts/Get-DriverPackage.ps1):

```powershell
./scripts/Get-DriverPackage.ps1 -OutputDirectory .artifacts/native -SevenZipPath 'C:\Program Files\7-Zip\7z.exe'
.artifacts/tool/Com0ComSharp.Tool.exe diagnose --package .artifacts/native
```

The script needs an existing 7-Zip executable. It verifies the installer hash and selects matching x86/x64 files without executing the installer. The NSIS archive has duplicate filenames for both architectures; blindly extracting/overwriting is insufficient.

Required files: `setupc.exe`, `setup.dll`, `com0com.sys`, `com0com.cat`, `com0com.inf`, `cncport.inf`, `comport.inf`. API driver staging does not create Start-menu shortcuts or an Add/Remove Programs entry. Native binaries are **not bundled** in the library, helper, or release.

## C# usage

```csharp
using Com0ComSharp;

var package = WindowsDiagnostics.FindInstalledPackage()
    ?? DriverPackage.Open(@"C:\Program Files\MyApp\com0com");
var client = new Com0ComClient(package, new ClientOptions
{
    Elevation = ElevationMode.Prompt,
    ElevationHelperPath = @"C:\Program Files\MyApp\Com0ComSharp.Tool.exe",
    // Explicit acknowledgement required for the supplied legacy driver.
    // This does not bypass Windows driver security policy.
    AllowLegacyDriver = true
});

// Read-only: no UAC and no setupc invocation.
var environment = client.GetEnvironment();
var existingPairs = client.GetPairs();

// Staging and creation share one UAC prompt through the helper.
// Select an unused index and store the pair your application owns.
var result = await client.ExecuteBatchAsync(new[]
{
    Com0ComCommand.InstallDriver(),
    Com0ComCommand.CreatePair(
        new PortSettings { PortName = "COM#" },
        new PortSettings { PortName = "COM#" }, pairIndex: 25)
});
result.ThrowIfFailed();

// A successful installer exit alone does not prove that Windows loaded the driver.
var endpoints = client.GetEnvironment().Devices
    .Where(d => d.PortId is "CNCA25" or "CNCB25").ToArray();
if (result.RebootRequired || endpoints.Length != 2 || endpoints.Any(d => !d.Healthy))
    throw new InvalidOperationException("Ports are not ready. Review problem codes and Code Integrity logs.");

var pair = client.GetPairs().Single(p => p.Index == 25);
Console.WriteLine($"{pair.A!.EffectiveName} <-> {pair.B!.EffectiveName}");

// Unspecified properties retain current values. Close open serial handles first.
(await client.ChangePortAsync("CNCA25", new PortSettings
{
    EmulateBaudRate = true,
    Cts = new PinMapping(PinSource.RemoteRts),
    Ring = new PinMapping(PinSource.On, Inverted: true)
})).ThrowIfFailed();
(await client.DestroyPairAsync(25)).ThrowIfFailed();
```

The [lifecycle sample](samples/Com0ComSharp.SmokeTest) demonstrates explicit driver testing, bidirectional binary transfer with `SerialPort`, configuration changes, and cleanup.

`RealPortName` is supported for an existing `PortName=COM#` endpoint. Upstream ignores it during creation, so the API rejects it in `CreatePair`:

```csharp
(await client.ChangePortAsync("CNCA25",
    new PortSettings { RealPortName = "COM42" })).ThrowIfFailed();
```

`PortName=COM42` names a port in com0com's custom class. `PortName=COM#` installs in Windows' standard Ports class, which ordinary COM enumeration usually expects. Use automatic allocation first, then change `RealPortName` if a fixed number is needed. Existing reservations are never force-released.

## Elevation and restricted PCs

See [deployment guidance](docs/deployment.md). An elevated installer uses the library directly. A normal desktop app can stay unelevated and launch the short-lived helper for one batch. The helper supports UAC approval/alternate administrator credentials, rechecks the native package fingerprint, allowlists operations, and returns results through an ACL-protected local named pipe bound to the launched helper's process ID. No persistent privileged service is installed.

Without `ElevationHelperPath`, Windows is asked to elevate `setupc` for each command. `ElevationMode.RequireAdministrator` returns `ElevationRequired` instead of prompting. UAC refusal returns `ElevationDenied`. Silent setup suppresses optional com0com dialogs; Windows UAC, publisher prompts, and enterprise policy cannot be hidden or bypassed.

Batches stop on the first failure or reboot requirement and preserve completed results. They are not transactions: completed changes remain after cancellation/failure. Through the helper, cancelling/closing the parent connection requests cancellation of remaining work. Direct per-command elevation may leave a privileged command running if Windows prevents the standard user from terminating it. Inspect state before retrying.

`UninstallDriverAsync()` removes **all com0com pairs and the driver system-wide**, including other applications' pairs. Prefer `DestroyPairAsync(index)` for app cleanup. Global enable/disable and INF cleanup similarly affect all com0com users.

## License and validation

Managed code is [MIT licensed](LICENSE). Separate upstream com0com binaries retain their GPL license. This repository links to pinned upstream source and does not redistribute native binaries.

Builds/unit tests validate managed behavior, native diagnostics, parsing, validation, batching, and process setup. They do not establish kernel compatibility, UAC under every policy, or serial-data transfer. Evidence and remaining limits are recorded in the [verification report](docs/driver-verification.md).
