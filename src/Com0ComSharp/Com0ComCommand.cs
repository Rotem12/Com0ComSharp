using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Com0ComSharp;

public enum Com0ComOperation
{
    InstallDriver, CreatePair, RemovePair, ChangePort, List, Help, BusyNames,
    UpdateDriver, ReloadDriver, UninstallDriver, EnableAll, DisableAll,
    CleanOldInfFiles, UpdateFriendlyNames, ListFriendlyNames, CreateNamedPair, CreateNamedConnector
}

/// <summary>An allowlisted setupc operation; there is no arbitrary shell command interface.</summary>
public sealed record Com0ComCommand
{
    public Com0ComCommand() { }

    /// <summary>Constructor-based typed command for consumers using C# 7.3.</summary>
    [SetsRequiredMembers]
    public Com0ComCommand(Com0ComOperation operation, int? pairIndex = null, string? portId = null,
        PortSettings? portA = null, PortSettings? portB = null, string? pattern = null,
        bool deferDriverUpdate = false, int waitSeconds = 30)
        : this(operation, false, pairIndex, portId, portA, portB, pattern, deferDriverUpdate, waitSeconds) { }

    /// <summary>Optionally selects the standard Windows Ports class for named creation.</summary>
    [SetsRequiredMembers]
    public Com0ComCommand(Com0ComOperation operation, bool useStandardPortsClass, int? pairIndex = null, string? portId = null,
        PortSettings? portA = null, PortSettings? portB = null, string? pattern = null,
        bool deferDriverUpdate = false, int waitSeconds = 30)
    {
        Operation = operation; PairIndex = pairIndex; PortId = portId;
        PortA = portA; PortB = portB; Pattern = pattern;
        DeferDriverUpdate = deferDriverUpdate; WaitSeconds = waitSeconds;
        UseStandardPortsClass = useStandardPortsClass;
    }

    public required Com0ComOperation Operation { get; init; }
    public int? PairIndex { get; init; }
    public string? PortId { get; init; }
    public PortSettings? PortA { get; init; }
    public PortSettings? PortB { get; init; }
    public string? Pattern { get; init; }
    public bool DeferDriverUpdate { get; init; }
    public int WaitSeconds { get; init; } = 30;
    /// <summary>Uses the slower standard Windows Ports setup class when a consumer specifically requires that Device Manager category.</summary>
    public bool UseStandardPortsClass { get; init; }

    public static Com0ComCommand InstallDriver() => new() { Operation = Com0ComOperation.InstallDriver };
    public static Com0ComCommand CreatePair(PortSettings? portA = null, PortSettings? portB = null, int? pairIndex = null, bool deferDriverUpdate = false)
        => new() { Operation = Com0ComOperation.CreatePair, PortA = portA, PortB = portB, PairIndex = pairIndex, DeferDriverUpdate = deferDriverUpdate };
    /// <summary>Creates Windows COM ports with the requested names.</summary>
    public static Com0ComCommand CreateNamedPair(string portA, string portB, bool emulateBaudRate = false)
        => new() { Operation = Com0ComOperation.CreateNamedPair,
            PortA = new(portName: ComPortNames.Normalize(portA), emulateBaudRate: emulateBaudRate),
            PortB = new(portName: ComPortNames.Normalize(portB), emulateBaudRate: emulateBaudRate) };
    /// <summary>Creates a standard COM endpoint with a hidden paired endpoint and optional baud-rate emulation.</summary>
    public static Com0ComCommand CreateNamedConnector(string portName, bool emulateBaudRate = false)
        => new() { Operation = Com0ComOperation.CreateNamedConnector,
            PortA = new(portName: ComPortNames.Normalize(portName), emulateBaudRate: emulateBaudRate),
            PortB = new(portName: "-", hiddenMode: true) };
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
            case Com0ComOperation.CreateNamedPair:
                ValidateNamedPair();
                // Install A directly in the standard Ports class. Name it before
                // converting B so automatic COM assignments cannot block one another.
                args.Add("install");
                args.Add((PortA! with { PortName = "COM#" }).ToParameterString());
                args.Add((PortB! with { PortName = "-" }).ToParameterString());
                break;
            case Com0ComOperation.CreateNamedConnector:
                ValidateNamedConnector();
                args.Add("install");
                args.Add((PortA! with { PortName = "COM#" }).ToParameterString());
                args.Add(PortB!.ToParameterString());
                break;
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
                if (Pattern is null || string.IsNullOrWhiteSpace(Pattern) || !Regex.IsMatch(Pattern, @"\A[A-Za-z0-9_?*#-]{1,64}\z", RegexOptions.CultureInvariant))
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

    internal void ValidateNamedPair()
    {
        if (PairIndex.HasValue || PortA?.RealPortName is not null || PortB?.RealPortName is not null)
            throw new ArgumentException("Named pairs allocate their own index; specify the desired COM names in PortName.");
        var a = ComPortNames.Normalize(PortA?.PortName);
        var b = ComPortNames.Normalize(PortB?.PortName);
        if (a == b) throw new ArgumentException("The two endpoints must have different COM names.");
    }

    internal void ValidateNamedConnector()
    {
        if (PairIndex.HasValue || PortA?.RealPortName is not null
            || PortB?.PortName != "-" || PortB.HiddenMode != true || PortB.RealPortName is not null)
            throw new ArgumentException("A named Connector needs one requested COM name and a hidden paired endpoint.");
        ComPortNames.Normalize(PortA?.PortName);
    }
}
