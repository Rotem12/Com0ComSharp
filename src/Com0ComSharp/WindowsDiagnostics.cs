using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Com0ComSharp;

/// <summary>Read-only discovery; does not invoke setupc or request elevation.</summary>
public static class WindowsDiagnostics
{
    public static bool IsAdministrator()
    {
        EnsureWindows();
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static EnvironmentReport InspectEnvironment()
    {
        EnsureWindows();
        var notes = new List<string>();
        bool? secureBoot = null, memoryIntegrity = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int enabled) secureBoot = enabled != 0;
        }
        catch (System.Security.SecurityException) { notes.Add("Secure Boot state is unavailable to this account."); }
        catch (UnauthorizedAccessException) { notes.Add("Secure Boot state is unavailable to this account."); }
        var info = new CodeIntegrityInfo { Length = (uint)Marshal.SizeOf<CodeIntegrityInfo>() };
        if (NtQuerySystemInformation(103, ref info, info.Length, out _) >= 0) memoryIntegrity = (info.Options & 0x400) != 0;
        else notes.Add("Running Memory Integrity state is unavailable.");
        notes.Add("Secure Boot, Memory Integrity, WDAC and the Windows Driver Policy can independently restrict drivers. There is no universal pre-install compatibility guarantee.");
        return new(GetOperatingSystemVersion(), RuntimeInformation.OSArchitecture.ToString(), IsAdministrator(), secureBoot, memoryIntegrity, GetDevices(), notes);
    }

    public static DriverPackage? FindInstalledPackage()
    {
        EnsureWindows();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\com0com");
            if (key?.GetValue("Install_Dir") is string directory) paths.Add(directory);
        }
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "com0com"));
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "com0com"));
        foreach (var path in paths)
        {
            if (!Directory.Exists(path)) continue;
            try { return DriverPackage.Open(path); }
            catch (FileNotFoundException) { }
            catch (PlatformNotSupportedException) { }
        }
        return null;
    }

    public static IReadOnlyList<DeviceStatus> GetDevices()
    {
        EnsureWindows();
        var handle = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x6); // PRESENT | ALLCLASSES
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var results = new List<DeviceStatus>();
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(handle, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                var service = Property(handle, ref data, 4);
                var hardwareIds = Property(handle, ref data, 1);
                if (!service.Equals("com0com", StringComparison.OrdinalIgnoreCase) && !hardwareIds.Contains("com0com", StringComparison.OrdinalIgnoreCase)) continue;
                var instance = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceIdW(handle, ref data, instance, instance.Capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var description = Property(handle, ref data, 12);
                if (description.Length == 0) description = Property(handle, ref data, 0);
                var physicalName = Property(handle, ref data, 14);
                var match = Regex.Match(physicalName + " " + instance + " " + description, @"CNC[AB][0-9]{1,6}(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var id = match.Success ? match.Value.ToUpperInvariant() : null;
                string? realName = null;
                var keyHandle = SetupDiOpenDevRegKey(handle, ref data, 1, 0, 1, 0x20019); // DIREG_DEV, KEY_READ
                if (keyHandle != new IntPtr(-1))
                {
                    using var safe = new Microsoft.Win32.SafeHandles.SafeRegistryHandle(keyHandle, true);
                    using var key = RegistryKey.FromHandle(safe);
                    realName = key.GetValue("PortName") as string;
                }
                var statusAvailable = CM_Get_DevNode_Status(out var status, out var problem, data.DevInst, 0) == 0;
                results.Add(new(instance.ToString(), description, id, realName, statusAvailable ? problem : null, statusAvailable && (status & 8) != 0));
            }
            return results;
        }
        finally { SetupDiDestroyDeviceInfoList(handle); }
    }

    /// <summary>Lists present endpoints and reads their configuration from the driver's registry keys.</summary>
    public static IReadOnlyList<VirtualPortPair> GetPairs()
    {
        var text = new StringBuilder();
        foreach (var device in GetDevices().Where(d => d.PortId is not null))
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\com0com\Parameters\" + device.PortId);
            var name = key?.GetValue("PortName") as string ?? device.PortId!;
            text.Append(device.PortId).Append(" PortName=").Append(name);
            if (name.Equals("COM#", StringComparison.OrdinalIgnoreCase) && device.PortName is not null) text.Append(",RealPortName=").Append(device.PortName);
            if (key is not null)
                foreach (var setting in key.GetValueNames().Where(n => !n.Equals("PortName", StringComparison.OrdinalIgnoreCase)))
                {
                    if (key.GetValue(setting) is not int number) continue;
                    var value = number.ToString(CultureInfo.InvariantCulture);
                    if (setting is "EmuBR" or "EmuOverrun" or "PlugInMode" or "ExclusiveMode" or "HiddenMode" or "AllDataBits") value = number == 0 ? "no" : "yes";
                    if (setting == "EmuNoise") value = (unchecked((uint)number) / 100000000m).ToString("0.########", CultureInfo.InvariantCulture);
                    if (setting is "cts" or "dsr" or "dcd" or "ri") value = DecodePin(unchecked((uint)number));
                    text.Append(',').Append(setting).Append('=').Append(value);
                }
            text.AppendLine();
        }
        return SetupOutputParser.ParsePairs(text.ToString());
    }

    /// <summary>Includes reservations for absent devices; never releases another application's reservation.</summary>
    public static IReadOnlyList<string> GetReservedComPortNames()
    {
        EnsureWindows();
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\COM Name Arbiter");
        if (key?.GetValue("ComDB") is not byte[] bits) return [];
        var names = new List<string>();
        for (var bit = 0; bit < bits.Length * 8; bit++) if ((bits[bit / 8] & (1 << (bit % 8))) != 0) names.Add("COM" + (bit + 1).ToString(CultureInfo.InvariantCulture));
        return names;
    }

    internal static void EnsureWindows() { if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("com0com is a Windows driver."); }
    private static string GetOperatingSystemVersion()
    {
        // Framework's Environment.OSVersion can report Windows 8 for an existing
        // app without a Windows 10 compatibility manifest. Native diagnostics
        // must describe the installed OS rather than that compatibility view.
        var version = new WindowsVersionInfo { Size = (uint)Marshal.SizeOf<WindowsVersionInfo>(), ServicePack = "" };
        return RtlGetVersion(ref version) >= 0
            ? $"Microsoft Windows NT {version.Major}.{version.Minor}.{version.Build}.0"
            : Environment.OSVersion.VersionString;
    }
    private static string DecodePin(uint number)
    {
        var value = (number & 0x7FFFFFFF) switch
        {
            1 => "rrts", 2 => "rdtr", 4 => "rout1", 8 => "rout2", 0x80 => "ropen",
            0x100 => "lrts", 0x200 => "ldtr", 0x400 => "lout1", 0x800 => "lout2", 0x8000 => "lopen", 0x10000000 => "on",
            _ => "unknown"
        };
        return ((number & 0x80000000) != 0 ? "!" : "") + value;
    }
    private static string Property(IntPtr handle, ref DeviceInfo data, uint property)
    {
        var bytes = new byte[8192];
        return SetupDiGetDeviceRegistryPropertyW(handle, ref data, property, out _, bytes, (uint)bytes.Length, out _) ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct CodeIntegrityInfo { public uint Length, Options; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsVersionInfo
    {
        public uint Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
    }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref WindowsVersionInfo version);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass, ref CodeIntegrityInfo info, uint length, out uint returnedLength);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr handle, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr handle, ref DeviceInfo data, uint property, out uint type, [Out] byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr handle, ref DeviceInfo data, StringBuilder buffer, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiOpenDevRegKey(IntPtr handle, ref DeviceInfo data, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
