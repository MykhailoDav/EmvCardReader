namespace EmvCardReader.Emv;

/// <summary>One decoded data element read from the card.</summary>
public sealed class EmvTagValue
{
    public required string Tag { get; init; }
    public required string Name { get; init; }
    public required string ValueHex { get; init; }
    public string? Decoded { get; init; }
    public required string Source { get; init; }   // where it was read from
}

/// <summary>One transaction parsed from the card's offline transaction log.</summary>
public sealed class EmvTransaction
{
    public string? Date { get; init; }
    public string? Time { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? Country { get; init; }
    public string? Type { get; init; }
    public int? Atc { get; init; }
    public string? Merchant { get; init; }
    public required string RawHex { get; init; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Date)) parts.Add(Date!);
        if (!string.IsNullOrEmpty(Time)) parts.Add(Time!);
        if (!string.IsNullOrEmpty(Amount)) parts.Add($"{Amount} {Currency}".Trim());
        if (Atc.HasValue) parts.Add($"ATC {Atc}");
        if (!string.IsNullOrEmpty(Merchant)) parts.Add(Merchant!);
        return parts.Count > 0 ? string.Join("  ·  ", parts) : RawHex;
    }
}

/// <summary>One EMV application present on the card.</summary>
public sealed class EmvApplication
{
    public required string Aid { get; init; }
    public string? Label { get; set; }
    public string? PreferredName { get; set; }
    public int? Priority { get; init; }
    public List<EmvTagValue> Tags { get; } = new();
    public List<EmvTransaction> Transactions { get; } = new();
}

/// <summary>Everything we managed to extract from a contactless card.</summary>
public sealed class EmvCardData
{
    public string? Uid { get; set; }
    public string? TechInfo { get; set; }

    // Convenience fields (best effort, pulled from whichever application has them).
    public string? Pan { get; set; }
    public string? PanSequenceNumber { get; set; }
    public string? Expiry { get; set; }
    public string? EffectiveDate { get; set; }
    public string? CardholderName { get; set; }
    public string? Track2 { get; set; }
    public string? ServiceCode { get; set; }
    public string? ApplicationLabel { get; set; }
    public string? Aid { get; set; }
    public string? IssuerCountry { get; set; }
    public string? Currency { get; set; }
    public string? Scheme { get; set; }
    public int? Atc { get; set; }
    public int? LastOnlineAtc { get; set; }
    public int? PinTriesRemaining { get; set; }

    public List<EmvApplication> Applications { get; } = new();

    /// <summary>Flat list of every tag read across every application/select.</summary>
    public List<EmvTagValue> AllTags { get; } = new();

    /// <summary>Full APDU command/response trace (for "show me literally everything").</summary>
    public List<string> ApduLog { get; } = new();

    /// <summary>Non-fatal warnings encountered while reading.</summary>
    public List<string> Warnings { get; } = new();
}
