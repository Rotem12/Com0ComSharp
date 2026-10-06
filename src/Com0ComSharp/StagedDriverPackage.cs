using System.Runtime.InteropServices;
using System.Text;

namespace Com0ComSharp;

internal static class StagedDriverPackage
{
    internal static bool IsInstalled(DriverPackage package, string? infDirectory = null, Func<string, bool>? isInDriverStore = null)
    {
        infDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        isInDriverStore ??= IsInDriverStore;
        // preinstall publishes these INFs before any device (or kernel service)
        // exists. Match the actual package, rather than the Services/com0com key.
        var remaining = new[] { "com0com.inf", "cncport.inf", "comport.inf" }
            .Select(name => Path.Combine(package.DirectoryPath, name))
            .Select(path => new { Hash = ManagementBrokerInstaller.HashFile(path), Length = new FileInfo(path).Length })
            .ToList();
        foreach (var published in Directory.EnumerateFiles(infDirectory, "oem*.inf"))
        {
            try
            {
                var length = new FileInfo(published).Length;
                if (!remaining.Any(x => x.Length == length)) continue;
                var hash = ManagementBrokerInstaller.HashFile(published);
                if (!remaining.Any(x => x.Hash == hash) || !isInDriverStore(published)) continue;
                remaining.RemoveAll(x => x.Hash == hash);
                if (remaining.Count == 0) return true;
            }
            catch (FileNotFoundException) { } // Another installer removed this INF during enumeration.
        }
        return false;
    }

    internal static bool IsInDriverStore(string publishedInf)
    {
        var location = new StringBuilder(260);
        return SetupGetInfDriverStoreLocationW(publishedInf, IntPtr.Zero, null, location, (uint)location.Capacity, out _)
            && File.Exists(location.ToString());
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetInfDriverStoreLocationW(string fileName, IntPtr alternatePlatform, string? locale, StringBuilder location, uint size, out uint required);
}
