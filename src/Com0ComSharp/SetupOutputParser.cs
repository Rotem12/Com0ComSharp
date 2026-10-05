using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Com0ComSharp;

public static class SetupOutputParser
{
    /// <summary>Parses native list output without discarding parameters added by future driver versions.</summary>
    public static IReadOnlyList<VirtualPortPair> ParsePairs(string output)
    {
        var ports = new Dictionary<string, VirtualPort>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"\A(?<id>CNC[AB][0-9]{1,6})\s+(?<parameters>.*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in match.Groups["parameters"].Value.Split(','))
            {
                var equals = item.IndexOf('=');
                if (equals > 0) values[item.Substring(0, equals).Trim()] = item.Substring(equals + 1).Trim();
            }
            if (!values.TryGetValue("PortName", out var name)) continue;
            var id = match.Groups["id"].Value.ToUpperInvariant();
            ports[id] = new(id, name, new ReadOnlyDictionary<string, string>(values));
        }
        return ports.Values.GroupBy(p => int.Parse(p.Id.Substring(4), System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(g => g.Key).Select(g => new VirtualPortPair(g.Key, g.FirstOrDefault(p => p.Id[3] == 'A'), g.FirstOrDefault(p => p.Id[3] == 'B'))).ToArray();
    }
}
