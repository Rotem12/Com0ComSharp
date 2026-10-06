using System.Globalization;
using System.Text.RegularExpressions;

namespace Com0ComSharp;

internal static class ComPortNames
{
    internal static string Normalize(string? name)
    {
        if (name is not null && Regex.IsMatch(name, @"\A[0-9]{1,4}\z", RegexOptions.CultureInvariant))
            name = "COM" + name;
        if (name is null || !Regex.IsMatch(name, @"\ACOM[1-9][0-9]{0,3}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || int.Parse(name.Substring(3), CultureInfo.InvariantCulture) > 4096)
            throw new ArgumentException("Use a standard Windows COM name from COM1 through COM4096.", nameof(name));
        return name.ToUpperInvariant();
    }
}
