# Deliberate Windows lifecycle test

This sample modifies the machine. It refuses to run if a com0com service, present devices, or a staged com0com catalog already exists. It requires an elevated launch and explicit `--allow-legacy`.

It verifies catalog integrity; stages the driver; creates a standard COM pair with index 812345; checks native endpoint readiness; transfers 512 binary bytes in each direction; changes a name and baud emulation; repeats transfer; and removes the pair and driver in `finally`. It preserves native logs, device state, reservations, and cleanup results in JSON. If Windows blocks the driver, transfer is marked unverified and cleanup is still attempted. Security settings are never modified.

When a published `Com0ComSharp.Tool.exe` is beside the smoke executable, the sample also checks helper result transport with native help from the elevated harness. This is a transport check; alternate administrator credentials and an unelevated parent require separate deployment testing.

```powershell
dotnet publish samples/Com0ComSharp.SmokeTest -c Release -r win-x64 --self-contained true -o .artifacts/smoke
$testArgs = @('--package', '"C:\path\to\native"', '--report', '"C:\path\to\report.json"', '--allow-legacy')
Start-Process .artifacts/smoke/Com0ComSharp.SmokeTest.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $testArgs -Wait
```

Choose an otherwise unused test system. A crash or reboot can interrupt cleanup; review the report and device state before retrying. Do not use this sample to uninstall a shared driver.
