# Driver verification — 2026-10-05

## Artifact inspected

Repository: [vovsoft/com0com](https://github.com/vovsoft/com0com), commit [`dca5e709afa498433777b36d3608a088231917ce`](https://github.com/vovsoft/com0com/tree/dca5e709afa498433777b36d3608a088231917ce).

Installer: `com0com v.3.0.0 setup 32+64-bit signed.exe`, committed directly; upstream has no GitHub releases. The readme describes an older/test-signing distribution, so binary signatures were inspected directly.

| Artifact | SHA-256 |
| --- | --- |
| Installer | `AE0DD19472F92BAB3165D370C3713616A3FFB708FAD785684759B04653705A71` |
| x64 `com0com.sys` | `E61C4943FB534F932193A81E8FBCB2F453E4B073A14159F9C161537B38D2BC38` |
| x64 `com0com.cat` | `C915BDDECB6779EBA6189EDA51AA1537B1D826E4CD3B21237929E1266B777D35` |
| x64 `setupc.exe` | `1A99693950ED8817D0F192671535FCCAD2BBCA585169A82606356A7A488D9B81` |
| x64 `setup.dll` | `CC5FB8C8F5259A14DBFC4053AFE1D08CECCD3B611169E97F3EA4E36EAFEA1049` |

`Get-AuthenticodeSignature` and the library's `WinVerifyTrust` checks report trusted driver/catalog signatures. Signer: **CN=Christos Nikolaou, C=GR**. Issuer: **GlobalSign CodeSigning CA - G2**. Thumbprint: `EE870261ABE4C69E4CEB332D9FEDBCF1794792C0`. The x64 driver was timestamped 2012-11-02.

SDK 10.0.26100.0 `signtool verify /kp /v com0com.sys` succeeds with zero warnings/errors. Its displayed cross-certificate chain passes through **Microsoft Code Verification Root**. `signtool verify /kp /c com0com.cat com0com.sys` also succeeds. The library checks catalog membership of `com0com.sys` and all three INF files; every trust result is 0.

These establish **legacy cross-signing**, not modern WHCP signing or a Windows 11 load. The installer is unsigned. No Microsoft hardware-publisher signature was observed. No verified replacement WHCP artifact was found in this investigation.

## Windows 11 limits

[Microsoft's signing requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-requirements--windows-vista-and-later-) also apply to virtual drivers. Modern drivers use Microsoft's submission/signing process; legacy exceptions and machine policies affect older binaries.

[The Windows Driver Policy](https://support.microsoft.com/en-us/windows/hardware/drivers/the-windows-driver-policy) removes default trust for legacy cross-signed drivers where enforced, while allowing WHCP-signed drivers and a legacy allowlist. Evaluation precedes enforcement. Success on one machine does not prove acceptance on another or after policy transitions. This investigation did not establish whether this hash is allowlisted.

Memory Integrity compatibility is an additional requirement. Its enabled state alone proves neither compatibility nor incompatibility. Valid Authenticode and legacy `/kp` results do not evaluate every current runtime Code Integrity/HVCI/enterprise constraint.

## Local evidence

Windows **10.0.26200**, x64; development session **not elevated**. Read-only native diagnostics report **Secure Boot disabled** and **Memory Integrity running**. No present com0com devices were found. VSPE was not modified. COM1/COM6/COM7 were reserved at baseline.

The original 0.1.0 lifecycle verification used .NET 8/.NET 10 Release builds and 36 tests per target. Version 0.2.0 adds Framework 4.6.2/4.8 assemblies and passes 51 tests per library target, with zero build warnings/errors. Read-only C# diagnostics agree with Windows/registry observations and verify catalog membership. See [Framework validation](framework-compatibility.md) for package-consumer evidence and the deferred Framework mutation/elevation tests.

The temporary lifecycle test **passed on this PC** with Memory Integrity running and Secure Boot disabled:

- Driver staging and kernel load succeeded. Both standard Ports-class endpoints and the bus reported `Started=true`, `ProblemCode=0`.
- Pair index 812345 was allocated as COM3/COM4. Exactly 512 binary bytes were compared successfully in each direction.
- CNCA812345 was renamed to COM42 and baud-rate emulation enabled. Readback matched; binary transfer passed again after the change.
- Pair removal and driver uninstall returned success. No com0com devices, driver service, driver-store INF packages, or driver file remained. COM reservations returned to their baseline; VSPE's bus still reported healthy. Secure Boot and running Memory Integrity state were unchanged.
- The helper returned native help through its protected named pipe and passed client-process validation. This transport check was performed from the elevated same-account harness. The harness itself was launched through Windows UAC; a separate unelevated-client/alternate-admin-credentials helper test has not been performed.

The machine-neutral result is saved in [the lifecycle evidence](evidence/windows11-x64-lifecycle.json). Two wrapper defects were found during initial live attempts and fixed before the passing run: setup.dll's eight-argument parser limit, and distinguishing an endpoint restart from the exact `Reboot required.` message. Regression tests cover both.

A local pass with Secure Boot disabled does not validate Secure Boot-enabled PCs, alternate administrator credentials, domain restrictions, x86 deployment, future reboots/policy transitions, or every update combination. ARM64 remains unsupported. Failure/cancellation/timeout branches need dedicated live policy testing in addition to the managed contract tests.

## Reproduce read-only checks

```powershell
./scripts/Get-DriverPackage.ps1 -OutputDirectory .artifacts/native -SevenZipPath 'C:\Program Files\7-Zip\7z.exe'
dotnet run --project src/Com0ComSharp.Tool -c Release -- diagnose --package .artifacts/native
signtool verify /kp /v .artifacts/native/com0com.sys
signtool verify /kp /c .artifacts/native/com0com.cat .artifacts/native/com0com.sys
```

After a failed live attempt, inspect the native log, [device problem codes](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-manager-error-messages), and `Microsoft-Windows-CodeIntegrity/Operational`. Code 52 concerns signature acceptance; other load failures use other codes. No security-policy bypass is implemented.
