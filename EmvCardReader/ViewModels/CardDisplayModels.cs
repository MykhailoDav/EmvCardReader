namespace EmvCardReader.ViewModels;

/// <summary>A label/value pair rendered in the summary section.</summary>
public sealed record KeyValueRow(string Key, string? Value);

/// <summary>A single bulleted line (warnings, transaction log entries).</summary>
public sealed record TextLine(string Text);

/// <summary>One decoded tag rendered inside an application section.</summary>
public sealed class TagRowView
{
    public required string Header { get; init; }      // "[tag] name"
    public string? Decoded { get; init; }
    public bool HasDecoded => !string.IsNullOrEmpty(Decoded);
    public required string ValueHex { get; init; }
    public required string Source { get; init; }
}

/// <summary>One EMV application section: its tags and (optional) transaction log.</summary>
public sealed class CardApplicationView
{
    public required string SectionTitle { get; init; }   // "Data — VISA"
    public required string Header { get; init; }         // "VISA  ·  A0000000031010"
    public IReadOnlyList<TagRowView> Tags { get; init; } = [];
    public IReadOnlyList<TextLine> Transactions { get; init; } = [];

    public bool HasTransactions => Transactions.Count > 0;
    public string TransactionsHeader => $"Transaction log ({Transactions.Count})";
}
