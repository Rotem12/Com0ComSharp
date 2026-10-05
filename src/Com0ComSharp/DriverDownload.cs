using System.Security.Cryptography;
using System.Net.Http;

namespace Com0ComSharp;

/// <summary>Downloads a pinned upstream installer. This does not execute or install it.</summary>
public static class DriverDownload
{
    public const string UpstreamCommit = "dca5e709afa498433777b36d3608a088231917ce";
    public const string InstallerSha256 = "AE0DD19472F92BAB3165D370C3713616A3FFB708FAD785684759B04653705A71";
    public static Uri InstallerUri { get; } = new($"https://raw.githubusercontent.com/vovsoft/com0com/{UpstreamCommit}/com0com%20v.3.0.0%20setup%2032%2B64-bit%20signed.exe");

    public static async Task<string> DownloadInstallerAsync(string destination, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            using var response = await client.GetAsync(InstallerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 5_000_000) throw new InvalidDataException("Unexpected installer size.");
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await RuntimeCompatibility.WaitAsync(input.ReadAsync(buffer, 0, buffer.Length, cancellationToken), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > 5_000_000) throw new InvalidDataException("Unexpected installer size.");
                    await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = File.OpenRead(temporary))
            using (var hash = SHA256.Create())
                if (!RuntimeCompatibility.ToHexString(hash.ComputeHash(stream)).Equals(InstallerSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The installer does not match the pinned SHA-256 hash.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); if (httpClient is null) client.Dispose(); }
    }
}
