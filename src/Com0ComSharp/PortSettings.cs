using System.Globalization;
using System.Text.RegularExpressions;

namespace Com0ComSharp;

public enum PinSource
{
    RemoteRts, RemoteDtr, RemoteOut1, RemoteOut2, RemoteOpen,
    LocalRts, LocalDtr, LocalOut1, LocalOut2, LocalOpen, On
}

public sealed record PinMapping(PinSource Source, bool Inverted = false)
{
    internal string Serialize() => (Inverted ? "!" : "") + (Source switch
    {
        PinSource.RemoteRts => "rrts", PinSource.RemoteDtr => "rdtr",
        PinSource.RemoteOut1 => "rout1", PinSource.RemoteOut2 => "rout2",
        PinSource.RemoteOpen => "ropen", PinSource.LocalRts => "lrts",
        PinSource.LocalDtr => "ldtr", PinSource.LocalOut1 => "lout1",
        PinSource.LocalOut2 => "lout2", PinSource.LocalOpen => "lopen",
        PinSource.On => "on", _ => throw new ArgumentOutOfRangeException(nameof(Source))
    });
}

/// <summary>Null properties leave a setting unspecified; changes preserve unspecified settings.</summary>
public sealed record PortSettings
{
    public PortSettings() { }

    /// <summary>Constructor-based configuration for consumers using C# 7.3; null settings remain unspecified.</summary>
    public PortSettings(string? portName = null, string? realPortName = null, bool? emulateBaudRate = null,
        bool? emulateOverrun = null, bool? plugInMode = null, bool? exclusiveMode = null, bool? hiddenMode = null,
        bool? allDataBits = null, decimal? noiseProbability = null, uint? additionalReadTotalTimeout = null,
        uint? additionalReadIntervalTimeout = null, PinMapping? cts = null, PinMapping? dsr = null,
        PinMapping? dcd = null, PinMapping? ring = null)
    {
        PortName = portName; RealPortName = realPortName;
        EmulateBaudRate = emulateBaudRate; EmulateOverrun = emulateOverrun;
        PlugInMode = plugInMode; ExclusiveMode = exclusiveMode; HiddenMode = hiddenMode; AllDataBits = allDataBits;
        NoiseProbability = noiseProbability; AdditionalReadTotalTimeout = additionalReadTotalTimeout;
        AdditionalReadIntervalTimeout = additionalReadIntervalTimeout;
        Cts = cts; Dsr = dsr; Dcd = dcd; Ring = ring;
    }

    /// <summary>Use COM# for automatic allocation in Windows' standard Ports class.</summary>
    public string? PortName { get; init; }
    /// <summary>Renames an existing COM endpoint with ChangePortAsync.</summary>
    public string? RealPortName { get; init; }
    public bool? EmulateBaudRate { get; init; }
    public bool? EmulateOverrun { get; init; }
    public bool? PlugInMode { get; init; }
    public bool? ExclusiveMode { get; init; }
    public bool? HiddenMode { get; init; }
    public bool? AllDataBits { get; init; }
    public decimal? NoiseProbability { get; init; }
    public uint? AdditionalReadTotalTimeout { get; init; }
    public uint? AdditionalReadIntervalTimeout { get; init; }
    public PinMapping? Cts { get; init; }
    public PinMapping? Dsr { get; init; }
    public PinMapping? Dcd { get; init; }
    public PinMapping? Ring { get; init; }

    public string ToParameterString()
    {
        var values = new List<string>();
        if (PortName is not null) { ValidatePortName(PortName); values.Add("PortName=" + PortName); }
        if (RealPortName is not null)
        {
            if (!Regex.IsMatch(RealPortName, @"\ACOM[1-9][0-9]{0,8}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new ArgumentException("RealPortName must be COM followed by a positive number.");
            values.Add("RealPortName=" + RealPortName);
        }
        void Flag(string key, bool? value) { if (value.HasValue) values.Add(key + "=" + (value.Value ? "yes" : "no")); }
        Flag("EmuBR", EmulateBaudRate); Flag("EmuOverrun", EmulateOverrun);
        Flag("PlugInMode", PlugInMode); Flag("ExclusiveMode", ExclusiveMode);
        Flag("HiddenMode", HiddenMode); Flag("AllDataBits", AllDataBits);
        if (NoiseProbability is decimal probability)
        {
            if (probability < 0 || probability > 0.99999999m || decimal.Round(probability, 8) != probability)
                throw new ArgumentOutOfRangeException(nameof(NoiseProbability), "Use 0 through 0.99999999 with at most eight decimal places.");
            values.Add("EmuNoise=" + probability.ToString("0.########", CultureInfo.InvariantCulture));
        }
        if (AdditionalReadTotalTimeout.HasValue) values.Add("AddRTTO=" + AdditionalReadTotalTimeout.Value.ToString(CultureInfo.InvariantCulture));
        if (AdditionalReadIntervalTimeout.HasValue) values.Add("AddRITO=" + AdditionalReadIntervalTimeout.Value.ToString(CultureInfo.InvariantCulture));
        if (Cts is not null) values.Add("cts=" + Cts.Serialize());
        if (Dsr is not null) values.Add("dsr=" + Dsr.Serialize());
        if (Dcd is not null) values.Add("dcd=" + Dcd.Serialize());
        if (Ring is not null) values.Add("ri=" + Ring.Serialize());
        return values.Count == 0 ? "*" : string.Join(",", values);
    }

    internal static void ValidatePortName(string value)
    {
        if (value is "-" or "*" || value.Equals("COM#", StringComparison.OrdinalIgnoreCase)) return;
        if (value.Length > 12 || !Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Port names must have 1-12 ASCII letters, digits or underscores and start with a letter.");
    }
}
