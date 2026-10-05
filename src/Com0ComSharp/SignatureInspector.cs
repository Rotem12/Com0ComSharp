using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Com0ComSharp;

public sealed record SignatureReport(string File, int TrustResult, string? Signer, string? Issuer)
{
    public bool AuthenticodeTrusted => TrustResult == 0;
    // Publisher identity is evidence about signing provenance, not a prediction of a machine's CI policy.
    public bool MicrosoftHardwarePublisher => AuthenticodeTrusted &&
        Signer?.Contains("Microsoft Windows Hardware Compatibility Publisher", StringComparison.OrdinalIgnoreCase) == true;
}

public static class SignatureInspector
{
    private static readonly Guid AuthenticodeAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureReport Inspect(string path)
    {
        WindowsDiagnostics.EnsureWindows();
        path = Path.GetFullPath(path);
        var info = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var result = Verify(info, 1);
        string? subject = null, issuer = null;
        try
        {
            // X509CertificateLoader does not extract signer certificates from PE/catalog files.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            subject = certificate.Subject; issuer = certificate.Issuer;
        }
        catch (System.Security.Cryptography.CryptographicException) { }
        return new(path, result, subject, issuer);
    }

    /// <summary>Checks both catalog trust and membership; checking the catalog signature alone is insufficient.</summary>
    public static int VerifyCatalogMember(string catalogPath, string memberPath)
    {
        WindowsDiagnostics.EnsureWindows();
        using var file = File.OpenRead(memberPath);
        var finalResult = unchecked((int)0x800B0100);
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            try
            {
                uint size = 0;
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle.DangerousGetHandle(), ref size, null, 0)) continue;
                var hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle.DangerousGetHandle(), ref size, hash, 0)) continue;
                var pin = GCHandle.Alloc(hash, GCHandleType.Pinned);
                try
                {
                    var info = new TrustCatalog
                    {
                        Size = (uint)Marshal.SizeOf<TrustCatalog>(), CatalogPath = Path.GetFullPath(catalogPath),
                        MemberTag = RuntimeCompatibility.ToHexString(hash), MemberPath = Path.GetFullPath(memberPath),
                        MemberFile = file.SafeFileHandle.DangerousGetHandle(), Hash = pin.AddrOfPinnedObject(), HashSize = size, Admin = admin
                    };
                    finalResult = Verify(info, 2);
                    if (finalResult == 0) return 0;
                }
                finally { pin.Free(); }
            }
            finally { CryptCATAdminReleaseContext(admin, 0); }
        }
        return finalResult;
    }

    private static int Verify<T>(T info, uint unionChoice) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(info, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = unionChoice, Info = pointer, StateAction = 1, ProviderFlags = 0x1000 };
        var action = AuthenticodeAction;
        try { return WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<T>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr File, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustCatalog
    {
        public uint Size, Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string CatalogPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberPath;
        public IntPtr MemberFile, Hash;
        public uint HashSize;
        public IntPtr Context, Admin;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr Info;
        public uint StateAction;
        public IntPtr StateData, Url;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string algorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint size, [Out] byte[]? hash, uint flags);
    [DllImport("wintrust.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
