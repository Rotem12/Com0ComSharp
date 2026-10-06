# Native driver test

Run on an unused test PC from an administrator terminal. This sample installs the native driver, creates a pair, transfers bytes in both directions, changes settings, and removes the pair and driver. It refuses an existing com0com installation.

```powershell
dotnet publish samples/Com0ComSharp.SmokeTest -c Release -r win-x64 --self-contained true -o .artifacts/smoke
.\.artifacts\smoke\Com0ComSharp.SmokeTest.exe --package C:\Test\com0com --report C:\Test\report.json --allow-legacy
```

This exercises native driver commands. The automated tests separately cover the API and broker transport. Review the JSON report after a failure or interrupted cleanup.
