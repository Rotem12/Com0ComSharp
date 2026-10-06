using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Com0ComSharp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            // These calls inspect Windows without launching setupc or requesting UAC.
            var environment = WindowsDiagnostics.InspectEnvironment();
            Console.WriteLine(RuntimeInformation.FrameworkDescription);
            Console.WriteLine(environment.OperatingSystem + " / " + environment.Architecture);
            Console.WriteLine("Pairs: " + WindowsDiagnostics.GetPairs().Count);
            Console.WriteLine("Reserved COM names: " + string.Join(", ", WindowsDiagnostics.GetReservedComPortNames()));
            var settings = new PortSettings(portName: "COM#", emulateBaudRate: true);
            Console.WriteLine("Example parameters: " + settings.ToParameterString());
            var options = new ClientOptions(elevation: ElevationMode.RequireAdministrator);
            Console.WriteLine("Example elevation policy: " + options.Elevation);
            var command = new Com0ComCommand(Com0ComOperation.BusyNames, pattern: "COM*");
            Console.WriteLine("Example typed command: " + command.Operation);

            if (args.Length == 0) return 0;
            if (args.Length == 2 && args[0] == "--package")
            {
                var package = DriverPackage.Open(args[1]);
                var signing = package.Inspect();
                Console.WriteLine("Catalog integrity verified: " + signing.IntegrityVerified);
                Console.WriteLine("Driver signer: " + signing.Driver.Signer);
                var api = new Com0ComApi(args[1], new ClientOptions(elevation: ElevationMode.RequireAdministrator));
                Console.WriteLine("Simple API devices: " + api.GetDevicesCount());
                foreach (var device in api.GetDevices())
                    Console.WriteLine("Pair " + api.GetDeviceInfo(device.Index).Index);
                return signing.IntegrityVerified ? 0 : 1;
            }
            if (args.Length == 2 && args[0] == "--download")
            {
                Console.WriteLine("Pinned installer: " + await DriverDownload.DownloadInstallerAsync(args[1]));
                return 0;
            }
            throw new ArgumentException("Usage: Com0ComSharp.Framework.exe [--package DIRECTORY | --download NEW_FILE.exe]");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    // A migration example, compiled as C# 7.3 but never run by this read-only sample.
    private static void PairLifecycleExample(string nativeDirectory)
    {
        var helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Com0ComSharp.Tool.exe");
        var api = new Com0ComApi(nativeDirectory, new ClientOptions(
            elevation: ElevationMode.Prompt, elevationHelperPath: helper, allowLegacyDriver: true));
        int deviceId = api.CreateDevice(21, emulateBaudRate: true);
        api.DestroyDevice(deviceId);
        api.Stop(); // Removes every com0com pair; the shared driver remains installed.
    }
}
