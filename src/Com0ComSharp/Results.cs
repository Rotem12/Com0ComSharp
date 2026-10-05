namespace Com0ComSharp;

public enum ElevationMode { Prompt, RequireAdministrator }
public enum FailureKind { None, ElevationRequired, ElevationDenied, DriverBlocked, AccessDenied, ProcessFailed, TimedOut, Cancelled, HelperFailed, PortNameInUse, RebootRequired }

public sealed record CommandResult(Com0ComOperation Operation, int ExitCode, string Output, FailureKind Failure, bool RebootRequired = false)
{
    /// <summary>For named pair creation, the actual allocated ID, also retained when later configuration fails.</summary>
    public int? CreatedPairIndex { get; init; }
    public bool Success => ExitCode is 0 or 3010 or 1641 && Failure == FailureKind.None;
    public void ThrowIfFailed() { if (!Success) throw new Com0ComException(this); }
    internal static CommandResult FromExit(Com0ComOperation operation, int exitCode, string output)
    {
        var reboot = exitCode is 3010 or 1641 || output.Split('\n').Any(line => line.Trim().Equals("Reboot required.", StringComparison.OrdinalIgnoreCase));
        var failure = exitCode == 0 || exitCode is 3010 or 1641 ? FailureKind.None : FailureKind.ProcessFailed;
        // setupc usually collapses Windows errors to exit code 1. Preserve its entire log for diagnosis.
        if (failure != FailureKind.None)
        {
            if (output.Contains("0x00000241", StringComparison.OrdinalIgnoreCase) || output.Contains("0x000004FB", StringComparison.OrdinalIgnoreCase) || output.Contains("Code 52", StringComparison.OrdinalIgnoreCase)) failure = FailureKind.DriverBlocked;
            else if (output.Contains("0x00000005", StringComparison.OrdinalIgnoreCase)) failure = FailureKind.AccessDenied;
        }
        return new(operation, exitCode, output, failure, reboot);
    }
}

public sealed record BatchResult(IReadOnlyList<CommandResult> Results, int RequestedCount)
{
    public bool Success => Results.Count == RequestedCount && Results.All(x => x.Success);
    public bool RebootRequired => Results.Any(x => x.RebootRequired);
    public void ThrowIfFailed()
    {
        foreach (var result in Results) result.ThrowIfFailed();
        if (!Success) throw new InvalidOperationException("The helper did not complete every requested operation.");
    }
}

public sealed class Com0ComException(CommandResult result) : Exception($"{result.Operation} failed ({result.Failure}, exit {result.ExitCode}): {result.Output}")
{
    public CommandResult Result { get; } = result;
}

public sealed record ClientOptions
{
    public ClientOptions() { }

    /// <summary>Constructor-based options for consumers using C# 7.3.</summary>
    public ClientOptions(ElevationMode elevation = ElevationMode.Prompt, TimeSpan? timeout = null,
        string? elevationHelperPath = null, bool allowLegacyDriver = false)
    {
        Elevation = elevation; Timeout = timeout ?? TimeSpan.FromMinutes(3);
        ElevationHelperPath = elevationHelperPath; AllowLegacyDriver = allowLegacyDriver;
    }

    public ElevationMode Elevation { get; init; } = ElevationMode.Prompt;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Path to the published Com0ComSharp.Tool.exe, enabling one UAC prompt per batch.</summary>
    public string? ElevationHelperPath { get; init; }
    /// <summary>Legacy packages need an explicit opt-in. A valid Authenticode signature does not prove Windows acceptance.</summary>
    public bool AllowLegacyDriver { get; init; }
}

public sealed record VirtualPort(string Id, string PortName, IReadOnlyDictionary<string, string> Parameters)
{
    public string EffectiveName => Parameters.TryGetValue("RealPortName", out var real) ? real : PortName;
}

public sealed record VirtualPortPair(int Index, VirtualPort? A, VirtualPort? B);

/// <summary>Observed device status, independent of installer return codes.</summary>
public sealed record DeviceStatus(string InstanceId, string Description, string? PortId, string? PortName, uint? ProblemCode, bool Started)
{
    public bool Healthy => Started && ProblemCode == 0;
}

public sealed record EnvironmentReport(string OperatingSystem, string Architecture, bool IsAdministrator, bool? SecureBootEnabled, bool? MemoryIntegrityRunning, IReadOnlyList<DeviceStatus> Devices, IReadOnlyList<string> Notes)
{
    public bool DriverRunning => Devices.Any(d => d.Healthy && d.InstanceId.StartsWith("ROOT\\COM0COM", StringComparison.OrdinalIgnoreCase));
}
