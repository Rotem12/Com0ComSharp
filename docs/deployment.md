# Deployment and elevation

## Recommended flow

1. Discover the installed package and inspect present devices without UAC. Reuse already provisioned ports when healthy.
2. Before first provisioning, explain that administrator approval is required. Inspect the package; the supplied legacy version also needs an explicit compatibility acknowledgement.
3. Deploy reviewed native files and the helper in an administrator-protected application directory. For managed PCs, have IT provision a validated driver and app-owned pairs through the normal elevated installer/device-management system.
4. Submit staging and port configuration as one batch. The helper requests UAC once when needed, executes sequentially, and exits. Omit the helper path for direct execution from an already elevated installer. Use the complete Framework 4.8 helper archive or the self-contained modern helper, which includes its own runtime.
5. Check reboot requirements, device problem codes, and `Healthy` status. Perform an application-level serial handshake before enabling dependent features.
6. Store the pair index/names your app owns. Remove only those pairs during app uninstall; do not globally uninstall a shared driver automatically.

Driver staging, device creation/removal, and configuration changes require administrator rights. After provisioning, ordinary serial I/O generally does not require admin rights; device permissions, open handles, and enterprise policy can still restrict access. Read-only diagnostics also depend on access to native/registry resources.

`setupc.exe` embeds `requireAdministrator`, including for read-only commands. The library's normal discovery uses SetupAPI, Configuration Manager, and registry reads. Native `Help`, `List`, `BusyNames`, and `ListFriendlyNames` remain available as typed commands, but invoking the executable requests elevation.

## Outcomes

| Result | Meaning / response |
| --- | --- |
| `ElevationRequired` | Use an elevated installer or select `ElevationMode.Prompt`. |
| `ElevationDenied` | UAC was cancelled/refused. Offer a deliberate retry or IT deployment. |
| `AccessDenied` | Inspect native logs, file permissions, and machine policy. |
| `PortNameInUse` | A requested standard COM name is present or reserved; named creation stops before creating a pair. |
| `DriverBlocked` | A recognized signing/load error. Other signing failures can arrive as `ProcessFailed`; inspect device/Code Integrity evidence. |
| `ProcessFailed` | Preserve the exit/log. setupc often collapses Windows errors to exit 1. |
| `HelperFailed` | Check helper/runtime deployment, application-control policy, IPC, and package fingerprint. Changes may already have occurred. |
| `Cancelled` / `TimedOut` | Completed changes remain. Helper disconnect requests cancellation; direct elevation may continue if Windows prevents termination. Inspect before retrying. |
| `RebootRequired` | Stop the batch and let the user/admin arrange a reboot. The library never reboots. |

`CommandResult.Success` describes the native command, not device readiness. Staging does not start a device. A pair needs both specific endpoints healthy. `CreateNamedPair` additionally verifies names and both endpoint states. The CLI returns exit 2 when creation succeeded but readiness cannot be established.

The [simple API](vspe-migration.md) discovers an adjacent helper, stages a missing driver and creates a VSPE-style Connector or Pair under one UAC request. `Stop()` destroys every machine-wide pair and uninstalls the driver. Use the helper from the same release as the library.

The UAC refusal, alternate-credentials, and transport paths need validation under actual deployment policies. API support does not prove a domain permits elevation. WDAC/AppLocker may reject the helper or unsigned setup utility before the kernel checks the driver.

## Trust boundaries

The helper takes a bounded typed plan and fixed native filenames, checks a SHA-256 snapshot of required files after UAC, then rechecks catalog integrity. Results use a local named pipe whose ACL permits the caller, administrators, and SYSTEM; the parent checks the client process ID. No shell, persistent service, scheduled task, credential storage, or driver-policy edits are involved.

A fingerprint detects changes during UAC; it does not make an untrusted executable safe or eliminate filesystem races in a user-writable directory. Install reviewed native executables/dependencies and the helper into an administrator-protected folder. Sign the helper as part of product release. The upstream user-mode installer/setup tools are unsigned despite the driver/catalog signatures; inspecting a driver signature does not authenticate setup executables.

Signature checks use cached certificate retrieval without online revocation refresh. The kernel's current policy is the final load authority. The Microsoft publisher flag describes observed provenance, not a WHCP database lookup or compatibility guarantee. Catalog-only signed drivers are supported by checking member hashes.

Upstream setupc is an ANSI-era utility with a 1024-byte buffer and only eight argument slots. Its second-stage parser splits whitespace even inside quotes. The wrapper limits native arguments, waits for PnP completion in managed code, and captures stdout inside the helper. Direct per-command elevation uses a whitespace-free short log path; when none exists, use the helper. Validate non-ASCII installation/user paths on the target locale. Unsafe parameter delimiters and shell fragments are rejected.

## Restricted PC limits

Framework 4.6.2 through 4.8.1 applications use the same elevation flow. They can deploy the Framework helper when 4.8+ is installed, or the self-contained modern helper. See [Framework deployment details](framework-compatibility.md) for dependency DLLs, binding redirects, old C# consumers, and TLS settings.

Ordinary users cannot install this kernel driver or create its devices through a supported user-only path. When elevation or driver policy prevents deployment, use IT provisioning or obtain an accepted driver. The library never enables test signing, disables Secure Boot/Memory Integrity, removes Code Integrity policies, installs certificates, releases other COM reservations, or changes device-install policy.

Sources: [kernel signing requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-requirements--windows-vista-and-later-), [Windows Driver Policy](https://support.microsoft.com/en-us/windows/hardware/drivers/the-windows-driver-policy), [ShellExecute](https://learn.microsoft.com/en-us/windows/win32/shell/launch).
