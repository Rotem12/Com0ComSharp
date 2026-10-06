# Setup

1. Reference the release's `Com0ComSharp` NuGet package.
2. Extract `Com0ComSharp.Helpers.win-x64.zip` beside your application. It contains the setup helper and broker, works with all supported .NET versions, and needs no separate .NET installation.
3. Extract the native com0com driver files using 7-Zip:

   ```powershell
   .\scripts\Get-DriverPackage.ps1 -OutputDirectory C:\MyApp\com0com
   ```

Use the API normally; first use requests Windows administrator approval and installs both components. A standard user may need administrator credentials. Subsequent port operations use the broker, which starts automatically after reboot.

To run setup explicitly:

```csharp
var api = new Com0ComApi(@"C:\MyApp\com0com",
    new ClientOptions(allowLegacyDriver: true));
var setup = api.InstallDriver();
if (setup.RebootRequired) return; // Tell the user to restart Windows.
int id = api.CreateDevice(21, true);
```

Replace the NuGet package and both helpers together when updating. Older broker installations request one approval to update. Keep your native driver directory available to the application.

`api.UninstallDriver()` removes all com0com pairs, the driver, and broker with administrator approval. The library never restarts Windows or changes its security settings.
