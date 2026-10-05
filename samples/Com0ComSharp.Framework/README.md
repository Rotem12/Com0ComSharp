# .NET Framework consumer

Read-only console sample for .NET Framework 4.6.2, 4.7.2, 4.8, and 4.8.1. It exercises Windows diagnostics and the public settings/options API. Optional package inspection also exercises simple facade discovery. The sample compiles a VSPE-style pair lifecycle example without executing it. Package inspection and the pinned installer download never run an installer or request elevation.

```powershell
dotnet build samples/Com0ComSharp.Framework -c Release
samples/Com0ComSharp.Framework/bin/Release/net48/Com0ComSharp.Framework.exe
samples/Com0ComSharp.Framework/bin/Release/net48/Com0ComSharp.Framework.exe --package .artifacts/native
```

To verify the actual NuGet package rather than a project reference:

```powershell
dotnet pack src/Com0ComSharp -c Release -o .artifacts/packages
dotnet restore samples/Com0ComSharp.Framework -p:UsePackagedLibrary=true --configfile scripts/FrameworkConsumer.NuGet.config
dotnet build samples/Com0ComSharp.Framework -c Release --no-restore -p:UsePackagedLibrary=true
samples/Com0ComSharp.Framework/bin/Release/net48/Com0ComSharp.Framework.exe
```

The sample deliberately uses **C# 7.3** and constructor-based settings/options/commands. Modern C# callers can also use the existing init-property examples. The resulting application runs on .NET Framework and does not require a modern .NET runtime. The reference-assemblies package supplies build-time targeting packs; it does not install a runtime. On this Windows 11 PC all four executables run on the installed .NET Framework 4.8.1, since Framework 4.x updates replace earlier runtimes in place. They do not prove execution on an original 4.6.2 installation.

For a .NET Framework 4.6.2 application that uses `DriverDownload`, retain the two TLS AppContext switches shown in [App.config](App.config), so HTTP uses the Windows TLS policy. Framework 4.7+ applications already default to the system TLS policy. The library does not alter global `ServicePointManager` settings.
