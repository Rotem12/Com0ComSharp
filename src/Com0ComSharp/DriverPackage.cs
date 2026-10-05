using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Com0ComSharp;

public sealed record PackageInspection(SignatureReport Driver, SignatureReport Catalog, IReadOnlyDictionary<string, int> CatalogMembers)
{
    public bool IntegrityVerified => Catalog.AuthenticodeTrusted && CatalogMembers.Values.All(x => x == 0);
    public bool MicrosoftHardwareSigned => IntegrityVerified && Catalog.MicrosoftHardwarePublisher;
    public string Acceptance => "Actual Windows Code Integrity acceptance must be checked after device creation; signature checks do not prove Windows 11 compatibility.";
}

/// <summary>A complete extracted driver package or an existing com0com installation.</summary>
public sealed class DriverPackage
{
    internal static readonly string[] RequiredFiles = ["setupc.exe", "setup.dll", "com0com.sys", "com0com.cat", "com0com.inf", "cncport.inf", "comport.inf"];
    public string DirectoryPath { get; }
    public string SetupExecutable => Path.Combine(DirectoryPath, "setupc.exe");
    private DriverPackage(string directory) => DirectoryPath = directory;

    public static DriverPackage Open(string directory)
    {
        directory = Path.GetFullPath(directory);
        foreach (var file in RequiredFiles)
            if (!File.Exists(Path.Combine(directory, file))) throw new FileNotFoundException("Incomplete com0com package: " + file, Path.Combine(directory, file));
        var expected = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => (ushort)0x8664, Architecture.X86 => (ushort)0x014c,
            _ => throw new PlatformNotSupportedException("The upstream package contains x86/x64 drivers; ARM64 is unsupported.")
        };
        foreach (var file in new[] { "com0com.sys", "setupc.exe", "setup.dll" })
            if (ReadMachine(Path.Combine(directory, file)) != expected) throw new PlatformNotSupportedException(file + " does not match the Windows architecture. Do not mix x86 and x64 files.");
        return new(directory);
    }

    public PackageInspection Inspect()
    {
        var catalog = Path.Combine(DirectoryPath, "com0com.cat");
        var members = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in new[] { "com0com.sys", "com0com.inf", "cncport.inf", "comport.inf" })
            members[member] = SignatureInspector.VerifyCatalogMember(catalog, Path.Combine(DirectoryPath, member));
        return new(SignatureInspector.Inspect(Path.Combine(DirectoryPath, "com0com.sys")), SignatureInspector.Inspect(catalog), members);
    }

    internal IReadOnlyDictionary<string, string> Fingerprint() => RequiredFiles.ToDictionary(file => file, file =>
    {
        using var stream = File.OpenRead(Path.Combine(DirectoryPath, file));
        return Convert.ToHexString(SHA256.HashData(stream));
    }, StringComparer.OrdinalIgnoreCase);

    internal static ushort ReadMachine(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Invalid PE file: " + path);
        stream.Position = 60;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 6) throw new InvalidDataException("Invalid PE header: " + path);
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Invalid PE signature: " + path);
        return reader.ReadUInt16();
    }
}
