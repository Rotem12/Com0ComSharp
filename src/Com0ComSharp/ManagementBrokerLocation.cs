using Microsoft.Win32;

namespace Com0ComSharp;

internal static class ManagementBrokerLocation
{
    // Use the protected 64-bit registry view. FOLDERID_ProgramFilesX64 is
    // unsupported in x86 processes, and environment variables are caller-controlled.
    internal static string InstallRoot => Path.Combine(ProgramFiles(Environment.Is64BitOperatingSystem), "Com0ComSharp");

    internal static bool HasProtectedRegistration()
    {
        try { return ReadProtectedRegistration(); }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static bool ReadProtectedRegistration()
    {
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Com0ComSharpBroker");
        if (service?.GetValue("ImagePath") is not string commandLine) return false;
        commandLine = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        var end = commandLine.IndexOf("Com0ComSharp.Broker.exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return false;
        var executable = commandLine.Substring(0, end + "Com0ComSharp.Broker.exe".Length).Trim('"');
        if (executable.StartsWith(@"\??\", StringComparison.Ordinal)) executable = executable.Substring(4);
        if (!string.Equals(Path.GetFileName(executable), "Com0ComSharp.Broker.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable));
        var roots = new[] { ProgramFiles(false), ProgramFiles(Environment.Is64BitOperatingSystem) };
        return roots.Any(root => string.Equals(directory, Path.Combine(root, "Com0ComSharp"), StringComparison.OrdinalIgnoreCase));
    }

    private static string ProgramFiles(bool x64)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        using var windows = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion");
        var name = !x64 && Environment.Is64BitOperatingSystem ? "ProgramFilesDir (x86)" : "ProgramFilesDir";
        if (windows?.GetValue(name) is not string path || !Path.IsPathRooted(path))
            throw new InvalidDataException("Windows did not return its Program Files directory.");
        return Path.GetFullPath(path);
    }
}
