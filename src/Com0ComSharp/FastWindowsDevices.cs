using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Com0ComSharp;

// Uses the already staged, signed driver. Only the new root device is bound;
// the standard Ports class installer and setupc process are unnecessary.
internal static class FastWindowsDevices
{
    private static readonly Guid ClassId = new("df799e12-3c56-421b-b298-b6d3642bc878");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string Parameters = @"SYSTEM\CurrentControlSet\Services\com0com\Parameters";
    private const string LocalArbiter = @"SYSTEM\CurrentControlSet\Services\com0com\COM Name Arbiter";
    private const string Marker = "Com0ComSharpManaged";
    private const string ManagedPairs = @"SYSTEM\CurrentControlSet\Services\com0com\Com0ComSharp\Pairs";
    internal static bool Supported => !Environment.Is64BitOperatingSystem || Environment.Is64BitProcess;

    internal static async Task<CommandResult> CreateAsync(DriverPackage package, Com0ComCommand command, CancellationToken token)
    {
        _ = command.ToArguments();
        await Gate.WaitAsync(token).ConfigureAwait(false);
        var log = new StringBuilder();
        var claimed = new List<string>();
        int? index = null;
        var registered = false;
        var recorded = false;
        var timer = Stopwatch.StartNew();
        CommandResult Result(int code, FailureKind failure, string note = "", bool reboot = false)
            => new(command.Operation, code, log + note, failure, reboot) { CreatedPairIndex = registered ? index : null };
        try
        {
            var a = ComPortNames.Normalize(command.PortA!.PortName);
            var b = command.Operation == Com0ComOperation.CreateNamedConnector ? null : ComPortNames.Normalize(command.PortB!.PortName);
            var unavailable = new HashSet<string>(WindowsDiagnostics.GetUnavailableComPortNames(), StringComparer.OrdinalIgnoreCase);
            if (unavailable.Contains(a) || (b is not null && unavailable.Contains(b)))
                return Result(32, FailureKind.PortNameInUse, "A requested COM name is already present or reserved.");
            using var roots = new DeviceSet();
            var used = new HashSet<int>(roots.Roots().Select(r => r.Index));
            foreach (var record in Records()) used.Add(record.Index);
            using (var parameters = Registry.LocalMachine.OpenSubKey(Parameters))
                foreach (var key in parameters?.GetSubKeyNames() ?? [])
                    if (Regex.IsMatch(key, @"\ACNC[AB][0-9]{1,6}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        used.Add(int.Parse(key.Substring(4), CultureInfo.InvariantCulture));
            index = Enumerable.Range(0, 1000000).Where(n => !used.Contains(n)).Select(n => (int?)n).FirstOrDefault();
            if (!index.HasValue) return Result(-1, FailureKind.ProcessFailed, "No pair index is available.");
            using (var record = Registry.LocalMachine.CreateSubKey(ManagedPairs + "\\" + index, true)!)
                record.SetValue("Names", Array.Empty<string>(), RegistryValueKind.MultiString);
            recorded = true;
            foreach (var name in b is null ? new[] { a } : new[] { a, b })
            {
                token.ThrowIfCancellationRequested();
                Claim(name); claimed.Add(name);
                using var record = Registry.LocalMachine.OpenSubKey(ManagedPairs + "\\" + index, true)!;
                record.SetValue("Names", claimed.ToArray(), RegistryValueKind.MultiString);
            }
            WriteSettings("CNCA" + index, command.PortA with { PortName = a });
            WriteSettings("CNCB" + index, command.PortB! with { PortName = b ?? "-" });
            var data = roots.CreateRoot();
            // Persist the root identity before registration. Cancellation, a
            // failed install, or reboot must leave an ID that Stop can clean up.
            using (var record = Registry.LocalMachine.OpenSubKey(ManagedPairs + "\\" + index, true)!)
                record.SetValue("Root", roots.InstanceId(ref data), RegistryValueKind.String);
            roots.Register(ref data);
            registered = true;
            using (var key = roots.OpenKey(ref data, true))
            {
                key.SetValue("PortNum", index.Value, RegistryValueKind.DWord);
                key.SetValue(Marker, 1, RegistryValueKind.DWord);
            }
            log.AppendLine($"Root and COM reservations: {timer.Elapsed.TotalMilliseconds:F1} ms.");
            token.ThrowIfCancellationRequested();
            var binding = Stopwatch.StartNew();
            var reboot = await Task.Run(() => roots.Install(ref data, Path.Combine(package.DirectoryPath, "com0com.inf")), CancellationToken.None).ConfigureAwait(false);
            log.AppendLine($"Bind staged driver to new root: {binding.Elapsed.TotalMilliseconds:F1} ms.");
            if (reboot) return Result(3010, FailureKind.RebootRequired, "Restart Windows before using the ports.", true);
            var ready = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var devices = WindowsDiagnostics.GetPortDevices();
                var ids = new[] { "CNCA" + index, "CNCB" + index };
                var names = new[] { a, b };
                var endpoints = ids.Select(id => devices.SingleOrDefault(d => string.Equals(d.PortId, id, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (endpoints.All(d => d?.Healthy == true) && Enumerable.Range(0, 2).All(i => names[i] is null || string.Equals(endpoints[i]!.PortName, names[i], StringComparison.OrdinalIgnoreCase)))
                {
                    log.AppendLine($"Endpoints started: {ready.Elapsed.TotalMilliseconds:F1} ms; total {timer.Elapsed.TotalMilliseconds:F1} ms.");
                    return Result(0, FailureKind.None);
                }
                if (endpoints.Any(d => d?.ProblemCode is 48 or 52)) return Result(-1, FailureKind.DriverBlocked, "Windows blocked the driver. The partial pair can be destroyed using its returned ID.");
                if (endpoints.Any(d => d?.ProblemCode == 10)) return Result(-1, FailureKind.ProcessFailed, "Windows could not start an endpoint.");
                if (ready.Elapsed >= TimeSpan.FromSeconds(command.WaitSeconds)) return Result(-1, FailureKind.TimedOut, "The endpoints did not become ready. The partial pair remains.");
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return Result(-1, FailureKind.Cancelled, "Creation cancelled; a registered partial pair remains available for cleanup."); }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var code = e is Win32Exception native ? native.NativeErrorCode : -1;
            return Result(code, code == 32 ? FailureKind.PortNameInUse : code is 577 or 1275 ? FailureKind.DriverBlocked : code == 5 || e is UnauthorizedAccessException or System.Security.SecurityException ? FailureKind.AccessDenied : FailureKind.ProcessFailed, e.Message);
        }
        finally
        {
            try
            {
                if (!registered && recorded)
                {
                    foreach (var name in claimed) Release(name);
                    if (index.HasValue)
                    {
                        foreach (var id in new[] { "CNCA" + index, "CNCB" + index }) Registry.LocalMachine.DeleteSubKeyTree(Parameters + "\\" + id, false);
                        Registry.LocalMachine.DeleteSubKeyTree(ManagedPairs + "\\" + index, false);
                    }
                }
            }
            finally { Gate.Release(); }
        }
    }

    internal static async Task<CommandResult?> TryRemoveAsync(Com0ComCommand command, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var devices = new DeviceSet();
            var matching = devices.Roots().Where(r => r.Index == command.PairIndex).ToArray();
            if (matching.Length > 1) return new(command.Operation, -1, "Multiple roots have this pair index; removal was refused.", FailureKind.ProcessFailed);
            var root = matching.SingleOrDefault();
            var record = Records().SingleOrDefault(r => r.Index == command.PairIndex);
            if (root is not null && !root.Managed || root is null && record is null) return null;
            var index = command.PairIndex!.Value;
            var pair = WindowsDiagnostics.GetPairs().SingleOrDefault(p => p.Index == index);
            var names = new[] { pair?.A?.EffectiveName, pair?.B?.EffectiveName }
                .Concat(record?.Names ?? []).Concat(new[] { "CNCA" + index, "CNCB" + index }.Select(id =>
                {
                    using var key = Registry.LocalMachine.OpenSubKey(Parameters + "\\" + id);
                    return key?.GetValue("PortName") as string;
                })).Where(IsComName).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            token.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            var log = new StringBuilder();
            if (root is not null)
            {
                var data = root.Data;
                var reboot = await Task.Run(() => devices.Remove(ref data, index, log), CancellationToken.None).ConfigureAwait(false);
                if (reboot) return new(command.Operation, 3010, "Restart Windows to finish removing the pair; its reservations are retained.", FailureKind.RebootRequired, true);
            }
            else if (devices.RemoveChildren(index))
                return new(command.Operation, 3010, "Restart Windows to finish removing the endpoints; their reservations are retained.", FailureKind.RebootRequired, true);
            // Check inactive records too, before releasing names or the retry
            // ledger. A stopped endpoint is not necessarily fully uninstalled.
            while (PairDevicesRemain(index))
            {
                token.ThrowIfCancellationRequested();
                if (timer.Elapsed >= TimeSpan.FromSeconds(Math.Max(1, command.WaitSeconds))) return new(command.Operation, -1, "A device record remains; COM reservations were retained.", FailureKind.TimedOut);
                await Task.Delay(25, token).ConfigureAwait(false);
            }
            var stillUsed = new HashSet<string>(WindowsDiagnostics.GetPairs().Where(p => p.Index != index)
                .SelectMany(p => new[] { p.A?.EffectiveName, p.B?.EffectiveName }).Where(IsComName).Cast<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var other in Records().Where(r => r.Index != index))
                foreach (var name in CurrentReservationNames(other)) stillUsed.Add(name);
            foreach (var name in names.Where(n => !stillUsed.Contains(n))) Release(name);
            foreach (var id in new[] { "CNCA" + index, "CNCB" + index }) Registry.LocalMachine.DeleteSubKeyTree(Parameters + "\\" + id, false);
            Registry.LocalMachine.DeleteSubKeyTree(ManagedPairs + "\\" + index, false);
            return new(command.Operation, 0, log + $"Removed pair {index} and its reservations in {timer.Elapsed.TotalMilliseconds:F1} ms.", FailureKind.None);
        }
        catch (OperationCanceledException) { return new(command.Operation, -1, "Removal cancelled; retry this pair ID to finish cleanup.", FailureKind.Cancelled); }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var code = e is Win32Exception native ? native.NativeErrorCode : -1;
            return new(command.Operation, code, e.Message + " Retry this pair ID to finish cleanup.", code == 5 ? FailureKind.AccessDenied : FailureKind.ProcessFailed);
        }
        finally { Gate.Release(); }
    }

    internal static IReadOnlyList<VirtualPortPair> GetPairsForStop()
    {
        var pairs = WindowsDiagnostics.GetPairs().ToList();
        using var roots = new DeviceSet();
        foreach (var root in roots.Roots().Where(r => r.Managed))
            if (!pairs.Any(p => p.Index == root.Index)) pairs.Add(new(root.Index, null, null));
        foreach (var record in Records())
            if (!pairs.Any(p => p.Index == record.Index)) pairs.Add(new(record.Index, null, null));
        return pairs;
    }

    private static bool IsComName(string? name) => name is not null && Regex.IsMatch(name, @"\ACOM[1-9][0-9]{0,3}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && int.Parse(name.Substring(3), CultureInfo.InvariantCulture) <= 4096;
    private sealed record PairRecord(int Index, string? Root, string[] Names);
    private static IEnumerable<string> CurrentReservationNames(PairRecord record)
    {
        for (var side = 0; side < 2; side++)
        {
            using var key = Registry.LocalMachine.OpenSubKey(Parameters + "\\CNC" + (side == 0 ? "A" : "B") + record.Index);
            // setupc may have renamed a managed endpoint. The durable ledger's
            // original name must not keep another removed pair's name reserved.
            var name = ReservationName(key is not null, key?.GetValue("PortName") as string,
                side < record.Names.Length ? record.Names[side] : null);
            if (name is not null) yield return name;
        }
    }
    internal static string? ReservationName(bool parametersExist, string? current, string? original)
        => IsComName(parametersExist ? current : original) ? (parametersExist ? current : original) : null;
    private static IReadOnlyList<PairRecord> Records()
    {
        using var records = Registry.LocalMachine.OpenSubKey(ManagedPairs);
        var result = new List<PairRecord>();
        foreach (var name in records?.GetSubKeyNames() ?? [])
            if (int.TryParse(name, out var index) && index is >= 0 and < 1000000)
            {
                using var key = records!.OpenSubKey(name);
                result.Add(new(index, key?.GetValue("Root") as string, key?.GetValue("Names") as string[] ?? []));
            }
        return result;
    }

    internal static IReadOnlyDictionary<string, object> SettingsValues(PortSettings settings)
    {
        _ = settings.ToParameterString();
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (settings.PortName is not null and not "-" and not "*") values["PortName"] = settings.PortName;
        void Flag(string name, bool? value) { if (value.HasValue) values[name] = value.Value ? 1 : 0; }
        Flag("EmuBR", settings.EmulateBaudRate); Flag("EmuOverrun", settings.EmulateOverrun); Flag("PlugInMode", settings.PlugInMode);
        Flag("ExclusiveMode", settings.ExclusiveMode); Flag("HiddenMode", settings.HiddenMode); Flag("AllDataBits", settings.AllDataBits);
        if (settings.NoiseProbability.HasValue) values["EmuNoise"] = (int)(settings.NoiseProbability.Value * 100000000m);
        if (settings.AdditionalReadTotalTimeout.HasValue) values["AddRTTO"] = unchecked((int)settings.AdditionalReadTotalTimeout.Value);
        if (settings.AdditionalReadIntervalTimeout.HasValue) values["AddRITO"] = unchecked((int)settings.AdditionalReadIntervalTimeout.Value);
        void Pin(string name, PinMapping? mapping)
        {
            if (mapping is null) return;
            var value = mapping.Source switch { PinSource.RemoteRts => 1u, PinSource.RemoteDtr => 2u, PinSource.RemoteOut1 => 4u, PinSource.RemoteOut2 => 8u, PinSource.RemoteOpen => 0x80u,
                PinSource.LocalRts => 0x100u, PinSource.LocalDtr => 0x200u, PinSource.LocalOut1 => 0x400u, PinSource.LocalOut2 => 0x800u, PinSource.LocalOpen => 0x8000u, PinSource.On => 0x10000000u,
                _ => throw new ArgumentOutOfRangeException(nameof(settings)) };
            values[name] = unchecked((int)(value | (mapping.Inverted ? 0x80000000u : 0u)));
        }
        Pin("cts", settings.Cts); Pin("dsr", settings.Dsr); Pin("dcd", settings.Dcd); Pin("ri", settings.Ring);
        return values;
    }

    private static void WriteSettings(string id, PortSettings settings)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Parameters + "\\" + id, true)!;
        foreach (var value in SettingsValues(settings)) key.SetValue(value.Key, value.Value, value.Value is string ? RegistryValueKind.String : RegistryValueKind.DWord);
    }

    private static void Claim(string name)
    {
        CheckCode(ComDBOpen(out var database));
        try
        {
            CheckCode(ComDBGetCurrentPortUsage(database, IntPtr.Zero, 0, 0, out var capacity));
            if (Number(name) > capacity) CheckCode(ComDBResizeDatabase(database, (Number(name) + 1023) / 1024 * 1024));
            CheckCode(ComDBClaimPort(database, Number(name), false, out _));
            try { SetLocalClaim(Number(name), true); }
            catch { ComDBReleasePort(database, Number(name)); throw; }
        }
        finally { ComDBClose(database); }
    }
    private static void Release(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(LocalArbiter);
        var bits = key?.GetValue("ComDB") as byte[];
        var bit = checked((int)Number(name) - 1);
        if (bits is null || bit / 8 >= bits.Length || (bits[bit / 8] & (1 << (bit % 8))) == 0) return;
        CheckCode(ComDBOpen(out var database));
        try { CheckCode(ComDBReleasePort(database, Number(name))); }
        finally { ComDBClose(database); }
        SetLocalClaim(Number(name), false);
    }
    private static uint Number(string name) => uint.Parse(ComPortNames.Normalize(name).Substring(3), CultureInfo.InvariantCulture);
    private static void SetLocalClaim(uint number, bool claimed)
    {
        // Preserve native setupc's ownership bitmap so its rename/uninstall
        // operations remain compatible and never release unrelated reservations.
        using var key = Registry.LocalMachine.CreateSubKey(LocalArbiter, true)!;
        var old = key.GetValue("ComDB") as byte[] ?? [];
        var bits = new byte[Math.Max(old.Length, checked((int)((number + 7) / 8)))];
        Array.Copy(old, bits, old.Length);
        var bit = checked((int)number - 1);
        if (claimed) bits[bit / 8] |= (byte)(1 << (bit % 8)); else bits[bit / 8] &= (byte)~(1 << (bit % 8));
        key.SetValue("ComDB", bits, RegistryValueKind.Binary);
    }

    private static bool PairDevicesRemain(int index)
    {
        using var children = new ChildDevices(index);
        if (children.Items.Count != 0) return true;
        using var roots = new DeviceSet();
        return roots.Roots().Any(r => r.Index == index);
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void CheckCode(int code) { if (code != 0) throw new Win32Exception(code); }

    private sealed record Root(int Index, DeviceInfo Data, bool Managed);
    private sealed class DeviceSet : IDisposable
    {
        internal readonly IntPtr Handle;
        internal DeviceSet()
        {
            var guid = ClassId;
            Handle = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0);
            if (Handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        internal IReadOnlyList<Root> Roots()
        {
            var results = new List<Root>();
            var records = Records();
            for (uint i = 0; ; i++)
            {
                var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(Handle, i, ref data)) { if (Marshal.GetLastWin32Error() == 259) break; Check(false); }
                var id = InstanceId(ref data);
                if (!id.StartsWith("ROOT\\COM0COM\\", StringComparison.OrdinalIgnoreCase)) continue;
                var record = records.SingleOrDefault(r => string.Equals(r.Root, id, StringComparison.OrdinalIgnoreCase));
                if (record is not null) { results.Add(new(record.Index, data, true)); continue; }
                using var key = OpenKey(ref data, false);
                if (key.GetValue("PortNum") is int index && index is >= 0 and < 1000000) results.Add(new(index, data, key.GetValue(Marker) is int managed && managed == 1));
            }
            return results;
        }
        internal DeviceInfo CreateRoot()
        {
            var guid = ClassId;
            var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            Check(SetupDiCreateDeviceInfoW(Handle, "com0com", ref guid, "com0com - bus for serial port pair emulator", IntPtr.Zero, 1, ref data));
            var hardware = Encoding.Unicode.GetBytes("root\\com0com\0\0");
            Check(SetupDiSetDeviceRegistryPropertyW(Handle, ref data, 1, hardware, (uint)hardware.Length));
            return data;
        }
        internal void Register(ref DeviceInfo data) => Check(SetupDiCallClassInstaller(0x19, Handle, ref data));
        internal string InstanceId(ref DeviceInfo data)
        {
            var id = new StringBuilder(512);
            Check(SetupDiGetDeviceInstanceIdW(Handle, ref data, id, id.Capacity, out _));
            return id.ToString();
        }
        internal RegistryKey OpenKey(ref DeviceInfo data, bool write)
        {
            var handle = write ? SetupDiCreateDevRegKeyW(Handle, ref data, 1, 0, 1, IntPtr.Zero, null) : SetupDiOpenDevRegKey(Handle, ref data, 1, 0, 1, 0x20019);
            if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var safe = new SafeRegistryHandle(handle, true);
            try { return RegistryKey.FromHandle(safe); }
            catch { safe.Dispose(); throw; }
        }
        internal bool Install(ref DeviceInfo data, string inf)
        {
            if (inf.Length >= 260) throw new IOException("The native driver INF path must be shorter than 260 characters.");
            var parameters = new InstallParameters { Size = (uint)Marshal.SizeOf<InstallParameters>(), DriverPath = "" };
            Check(SetupDiGetDeviceInstallParamsW(Handle, ref data, ref parameters));
            parameters.Flags |= 0x10000 | 0x800000; // DI_ENUMSINGLEINF | DI_QUIETINSTALL
            parameters.FlagsEx |= 0x800; // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
            parameters.DriverPath = inf;
            Check(SetupDiSetDeviceInstallParamsW(Handle, ref data, ref parameters));
            Check(SetupDiBuildDriverInfoList(Handle, ref data, 2));
            var driver = new DriverInfo { Size = (uint)Marshal.SizeOf<DriverInfo>(), Description = "", Manufacturer = "", Provider = "" };
            Check(SetupDiEnumDriverInfoW(Handle, ref data, 2, 0, ref driver));
            Check(SetupDiSetSelectedDriverW(Handle, ref data, ref driver));
            // The signed bus INF has no device co-installer or finish-install
            // action. Dispatch the installation request to its class installer
            // and the Windows default handler for this one selected driver.
            Check(SetupDiCallClassInstaller(2, Handle, ref data)); // DIF_INSTALLDEVICE
            return NeedsReboot(Handle, ref data);
        }
        internal bool Remove(ref DeviceInfo data, int index, StringBuilder log)
        {
            using var children = new ChildDevices(index);
            // A converted standard COM endpoint can have a serial enumerator
            // and grandchildren. Let Newdev walk any nontrivial subtree.
            if (!children.AreSimpleLeaves(data.DevInst))
            {
                log.AppendLine("Recursive removal for a nontrivial device tree.");
                Check(DiUninstallDevice(IntPtr.Zero, Handle, ref data, 0, out var reboot));
                if (reboot) return true;
            }
            else
            {
                log.AppendLine("Root-first removal of two CNC endpoints.");
                SetQuiet(Handle, ref data);
                Check(SetupDiCallClassInstaller(5, Handle, ref data)); // DIF_REMOVE
                if (NeedsReboot(Handle, ref data)) return true;
            }
            // Removing the parent stops both endpoints in one PnP operation.
            // DIF_REMOVE still needs to delete their now-phantom registry nodes.
            // Reopen by ID because the recursive fallback may have removed them.
            return RemoveChildren(index);
        }
        internal bool RemoveChildren(int index)
        {
            using var children = new ChildDevices(index);
            foreach (var child in children.Items)
            {
                var data = child;
                // A failed recursive removal can leave a standard endpoint
                // after its root disappeared. Its descendants still need the
                // complete Newdev cleanup when retrying through the ledger.
                if (data.ClassGuid != ClassId || CM_Get_Child(out _, data.DevInst, 0) != 0x0d)
                {
                    Check(DiUninstallDevice(IntPtr.Zero, children.Handle, ref data, 0, out var reboot));
                    if (reboot) return true;
                    continue;
                }
                SetQuiet(children.Handle, ref data);
                Check(SetupDiCallClassInstaller(5, children.Handle, ref data));
                if (NeedsReboot(children.Handle, ref data)) return true;
            }
            return false;
        }
        public void Dispose() => SetupDiDestroyDeviceInfoList(Handle);
    }

    private sealed class ChildDevices : IDisposable
    {
        internal readonly IntPtr Handle;
        internal readonly List<DeviceInfo> Items = new();
        internal ChildDevices(int index)
        {
            Handle = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (Handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                foreach (var name in new[] { "CNCA" + index, "CNCB" + index })
                {
                    var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                    if (SetupDiOpenDeviceInfoW(Handle, "COM0COM\\PORT\\" + name, IntPtr.Zero, 0, ref data)) Items.Add(data);
                    else if (unchecked((uint)Marshal.GetLastWin32Error()) != 0xe000020b) Check(false); // ERROR_NO_SUCH_DEVINST
                }
            }
            catch { Dispose(); throw; }
        }
        internal bool AreSimpleLeaves(uint root)
        {
            if (Items.Count != 2 || Items.Any(d => d.ClassGuid != ClassId)) return false;
            if (CM_Get_Child(out var first, root, 0) != 0) return false;
            var seen = new HashSet<uint>();
            for (var node = first; ;)
            {
                if (!seen.Add(node) || !Items.Any(d => d.DevInst == node)) return false;
                var childStatus = CM_Get_Child(out _, node, 0);
                if (childStatus != 0x0d) return false; // CR_NO_SUCH_DEVNODE: no grandchildren
                var siblingStatus = CM_Get_Sibling(out var next, node, 0);
                if (siblingStatus == 0x0d) return seen.Count == 2;
                if (siblingStatus != 0) return false;
                node = next;
            }
        }
        public void Dispose() => SetupDiDestroyDeviceInfoList(Handle);
    }
    private static InstallParameters ReadParameters(IntPtr handle, ref DeviceInfo data)
    {
        var parameters = new InstallParameters { Size = (uint)Marshal.SizeOf<InstallParameters>(), DriverPath = "" };
        Check(SetupDiGetDeviceInstallParamsW(handle, ref data, ref parameters));
        return parameters;
    }
    private static bool NeedsReboot(IntPtr handle, ref DeviceInfo data) => RequiresReboot(ReadParameters(handle, ref data).Flags);
    internal static bool RequiresReboot(uint flags) => (flags & (0x80 | 0x100)) != 0; // DI_NEEDRESTART | DI_NEEDREBOOT
    private static void SetQuiet(IntPtr handle, ref DeviceInfo data)
    {
        var parameters = ReadParameters(handle, ref data);
        parameters.Flags |= 0x800000; // DI_QUIETINSTALL
        Check(SetupDiSetDeviceInstallParamsW(handle, ref data, ref parameters));
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct InstallParameters
    {
        public uint Size, Flags, FlagsEx; public IntPtr Parent, Callback, Context, FileQueue, ClassInstallReserved; public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DriverInfo
    {
        public uint Size, DriverType; public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description, Manufacturer, Provider;
        public System.Runtime.InteropServices.ComTypes.FILETIME Date; public ulong Version;
    }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr handle, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr handle, ref DeviceInfo data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiCreateDeviceInfoW(IntPtr handle, string name, ref Guid guid, string description, IntPtr window, uint flags, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr handle, ref DeviceInfo data, uint property, byte[] value, uint size);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiCallClassInstaller(uint function, IntPtr handle, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiCreateDevRegKeyW(IntPtr handle, ref DeviceInfo data, uint scope, uint profile, uint type, IntPtr inf, string? section);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiOpenDevRegKey(IntPtr handle, ref DeviceInfo data, uint scope, uint profile, uint type, uint access);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstallParamsW(IntPtr handle, ref DeviceInfo data, ref InstallParameters parameters);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiSetDeviceInstallParamsW(IntPtr handle, ref DeviceInfo data, ref InstallParameters parameters);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiBuildDriverInfoList(IntPtr handle, ref DeviceInfo data, uint type);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDriverInfoW(IntPtr handle, ref DeviceInfo data, uint type, uint index, ref DriverInfo driver);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classId, IntPtr window);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiOpenDeviceInfoW(IntPtr handle, string id, IntPtr window, uint flags, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiSetSelectedDriverW(IntPtr handle, ref DeviceInfo data, ref DriverInfo driver);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Child(out uint child, uint parent, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Sibling(out uint sibling, uint node, uint flags);
    [DllImport("newdev.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DiUninstallDevice(IntPtr window, IntPtr handle, ref DeviceInfo data, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
    [DllImport("msports.dll")] private static extern int ComDBOpen(out IntPtr database);
    [DllImport("msports.dll")] private static extern int ComDBClose(IntPtr database);
    [DllImport("msports.dll")] private static extern int ComDBClaimPort(IntPtr database, uint number, [MarshalAs(UnmanagedType.Bool)] bool force, [MarshalAs(UnmanagedType.Bool)] out bool forced);
    [DllImport("msports.dll")] private static extern int ComDBReleasePort(IntPtr database, uint number);
    [DllImport("msports.dll")] private static extern int ComDBGetCurrentPortUsage(IntPtr database, IntPtr buffer, uint size, uint reportType, out uint capacity);
    [DllImport("msports.dll")] private static extern int ComDBResizeDatabase(IntPtr database, uint capacity);
}
