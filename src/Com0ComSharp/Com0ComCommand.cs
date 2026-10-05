using System.Globalization;
using System.Text.RegularExpressions;

namespace Com0ComSharp;

public enum Com0ComOperation
{
    InstallDriver, CreatePair, RemovePair, ChangePort, List, Help, BusyNames,
    UpdateDriver, ReloadDriver, UninstallDriver, EnableAll, DisableAll,
    CleanOldInfFiles, UpdateFriendlyNames, ListFriendlyNames
}

/// <summary>An allowlisted setupc operation; there is no arbitrary shell command interface.</summary>
public sealed record Com0ComCommand
{
    public required Com0ComOperation Operation { get; init; }
    public int? PairIndex { get; init; }
    public string? PortId { get; init; }
    public PortSettings? PortA { get; init; }
    public PortSettings? PortB { get; init; }
    public string? Pattern { get; init; }
    public bool DeferDriverUpdate { get; init; }
    public int WaitSeconds { get; init; } = 30;

    public static Com0ComCommand InstallDriver() => new() { Operation = Com0ComOperation.InstallDriver };
    public static Com0ComCommand CreatePair(PortSettings? portA = null, PortSettings? portB = null, int? pairIndex = null, bool deferDriverUpdate = false)
        => new() { Operation = Com0ComOperation.CreatePair, PortA = portA, PortB = portB, PairIndex = pairIndex, DeferDriverUpdate = deferDriverUpdate };
    public static Com0ComCommand RemovePair(int pairIndex) => new() { Operation = Com0ComOperation.RemovePair, PairIndex = pairIndex };
    public static Com0ComCommand ChangePort(string portId, PortSettings settings) => new() { Operation = Com0ComOperation.ChangePort, PortId = portId, PortA = settings };

    internal IReadOnlyList<string> ToArguments()
    {
        if (PairIndex is < 0 or > 999999) throw new ArgumentOutOfRangeException(nameof(PairIndex));
        if (WaitSeconds is < 0 or > 600) throw new ArgumentOutOfRangeException(nameof(WaitSeconds));
        if (DeferDriverUpdate && Operation != Com0ComOperation.CreatePair) throw new ArgumentException("DeferDriverUpdate is only valid for CreatePair.");
        // MainA in setup.dll has only eight argument slots. Completion waiting is
        // handled in managed code so --output plus indexed/deferred installs fit.
        var args = new List<string> { "--silent" };
        if (DeferDriverUpdate) args.Add("--no-update");
        switch (Operation)
        {
            case Com0ComOperation.InstallDriver: args.Add("preinstall"); break;
            case Com0ComOperation.CreatePair:
                if (PortA?.RealPortName is not null || PortB?.RealPortName is not null)
                    throw new ArgumentException("com0com ignores RealPortName during install; create COM# ports, then use ChangePort.");
                if (PortA?.PortName is { } a && PortB?.PortName is { } b && a is not "*" and not "-" && !a.Equals("COM#", StringComparison.OrdinalIgnoreCase) && a.Equals(b, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The two endpoints must have different names.");
                args.Add("install");
                if (PairIndex.HasValue) args.Add(PairIndex.Value.ToString(CultureInfo.InvariantCulture));
                args.Add(PortA?.ToParameterString() ?? "-"); args.Add(PortB?.ToParameterString() ?? "-");
                break;
            case Com0ComOperation.RemovePair:
                args.Add("remove"); args.Add((PairIndex ?? throw new ArgumentException("PairIndex is required.")).ToString(CultureInfo.InvariantCulture)); break;
            case Com0ComOperation.ChangePort:
                if (PortId is null || !Regex.IsMatch(PortId, @"\ACNC[AB][0-9]{1,6}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new ArgumentException("Use an endpoint identifier such as CNCA0 or CNCB0.");
                args.Add("change"); args.Add(PortId); args.Add((PortA ?? throw new ArgumentException("Settings are required.")).ToParameterString()); break;
            case Com0ComOperation.BusyNames:
                if (string.IsNullOrWhiteSpace(Pattern) || !Regex.IsMatch(Pattern, @"\A[A-Za-z0-9_?*#-]{1,64}\z", RegexOptions.CultureInvariant))
                    throw new ArgumentException("Busy-name patterns may contain ASCII port-name characters, * and ?.");
                args.Add("busynames"); args.Add(Pattern); break;
            case Com0ComOperation.EnableAll: args.AddRange(["enable", "all"]); break;
            case Com0ComOperation.DisableAll: args.AddRange(["disable", "all"]); break;
            default:
                args.Add(Operation switch
                {
                    Com0ComOperation.List => "list", Com0ComOperation.Help => "help",
                    Com0ComOperation.UpdateDriver => "update", Com0ComOperation.ReloadDriver => "reload",
                    Com0ComOperation.UninstallDriver => "uninstall", Com0ComOperation.CleanOldInfFiles => "infclean",
                    Com0ComOperation.UpdateFriendlyNames => "updatefnames", Com0ComOperation.ListFriendlyNames => "listfnames",
                    _ => throw new ArgumentOutOfRangeException(nameof(Operation))
                }); break;
        }
        return args;
    }
}
