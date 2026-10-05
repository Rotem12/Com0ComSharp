# .NET Framework compatibility

Version 0.3.0 contains `net462`, `net48`, `net8.0-windows`, and `net10.0-windows` assemblies in one NuGet package. NuGet selects `net462` for Framework 4.6.2/4.7/4.7.1/4.7.2 and `net48` for 4.8/4.8.1. Modern .NET 9 can use the .NET 8 assembly. Driver architecture and Windows signing policy are independent of the managed target.

All targets expose the same public operations. Framework-compatible code handles process arguments, event-based asynchronous process completion, cancellable waits, pipe ACLs, ANSI logs, hashing, and stream IO. A stopped wait does not roll back native changes. On Framework, process cancellation terminates the directly launched process when permitted; descendant processes can continue because Framework lacks the process-tree `Kill` overload. Windows elevation boundaries can prevent termination on either runtime.

Settings/options/typed commands retain init-property syntax for modern callers and add constructors usable from **C# 7.3**. The committed [sample](../samples/Com0ComSharp.Framework) uses C# 7.3 with nullable and implicit usings disabled, covering an older project's normal language settings. The library itself is built using a current SDK; consuming it does not require that SDK or a modern .NET runtime on end-user PCs.

## Helper deployment

- `Com0ComSharp.Tool.net48.zip`: Framework 4.8 helper, including its executable, config, and dependency DLLs. Extract the complete archive. Requires the installed Framework 4.8 or 4.8.1 runtime.
- `Com0ComSharp.Tool.exe`: modern self-contained Windows x64 helper. Framework applications can also use it without installing .NET 8. Publish from the .NET 8 target explicitly now that the tool has multiple targets.

The wire protocol remains typed JSON; both helpers use the same catalog validation, SHA-256 fingerprint check, operation allowlist, and protected result pipe. No persistent service or Windows security changes are added for Framework support.

## App configuration

Use the NuGet package so dependencies follow the selected target. Enable `AutoGenerateBindingRedirects` and `GenerateBindingRedirectsOutputType` in Framework executable projects and deploy the generated `.exe.config`. Projects calling `DriverDownload` also need a framework reference to `System.Net.Http`.

For a Framework 4.6.2 application that uses the optional download API, enable system TLS defaults in the application's config, as shown in [App.config](../samples/Com0ComSharp.Framework/App.config). Framework 4.7+ defaults to system TLS. The library leaves process-global `ServicePointManager` settings and registry policy untouched. See [Microsoft's TLS guidance](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls).

## Verification on 2026-10-05

- Release solution build: zero warnings/errors across all library and tool targets.
- 78 tests on each of the four library targets: 312 test executions. Coverage includes Windows argument parsing, process wait cancellation, protected pipe JSON responses, constructor-based API compatibility, bounded hash-verified downloads, VSPE Pair syntax, adaptive naming, device failures and partial results.
- C# 7.3 sample builds as a NuGet consumer for Framework 4.6.2, 4.7.2, 4.8, and 4.8.1, then runs read-only discovery and real catalog/member signature inspection on the Windows 11 x64 host. The older target also completes a SHA-256-verified HTTPS download using the sample's TLS configuration.
- OS version diagnostics use the native version result, avoiding Framework's unmanifested `Environment.OSVersion` compatibility value.

This PC has Framework 4.8.1 installed (release key 533509). Framework 4.x versions are [in-place updates](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements), so execution of the older-target assemblies here validates them on the installed 4.8.1 runtime. Original 4.6.2/4.7.x runtime installations, Framework-originated elevated driver mutations, UAC refusal/alternate credentials, and domain-policy scenarios remain untested. No elevation prompts or kernel-driver changes were performed for this compatibility update.
