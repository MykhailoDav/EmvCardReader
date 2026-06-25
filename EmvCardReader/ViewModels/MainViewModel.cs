using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmvCardReader.Emv;
using EmvCardReader.Services;

namespace EmvCardReader.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly INfcCardReader reader;
    private readonly IClipboardService clipboard;
    private EmvCardData? last;

    public MainViewModel(INfcCardReader reader, IClipboardService clipboard)
    {
        this.reader = reader;
        this.clipboard = clipboard;

        this.reader.CardRead += OnCardRead;
        this.reader.Status += OnStatus;
        this.reader.Error += OnError;

        status = this.reader.StatusText;
        isSupported = this.reader.IsAvailable;
    }

    // ---------------------------------------------------------------- state

    // NOTE: [ObservableProperty] fields must NOT be readonly — the generated
    // property setter assigns to them. Do not let IDE "make readonly" touch these.
    [ObservableProperty]
    private string status = "Checking NFC…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool isSupported = true;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _hasWarnings;

    [ObservableProperty]
    private bool _hasApdu;

    [ObservableProperty]
    private string? _apduTrace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApduToggleText))]
    private bool _isApduVisible;

    public string ApduToggleText => IsApduVisible ? "Hide raw APDU trace" : "Show raw APDU trace";

    public ObservableCollection<KeyValueRow> Summary { get; } = new();
    public ObservableCollection<CardApplicationView> Applications { get; } = new();
    public ObservableCollection<TextLine> Warnings { get; } = new();

    // ---------------------------------------------------------------- commands

    private bool CanScan => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private void Scan()
    {
        ClearResults();
        IsBusy = true;
        Status = "Waiting for card…";
        reader.StartListening();
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        if (last is null)
            return;
        await clipboard.SetTextAsync(BuildTextDump(last));
        Status = "Copied to clipboard.";
    }

    [RelayCommand]
    private void ToggleApdu() => IsApduVisible = !IsApduVisible;

    // ---------------------------------------------------------------- reader events

    private void OnStatus(object? sender, string message)
        => RunOnUi(() => Status = message);

    private void OnError(object? sender, string message)
        => RunOnUi(() =>
        {
            IsBusy = false;
            Status = $"⚠️ {message}";
        });

    private void OnCardRead(object? sender, EmvCardData data)
        => RunOnUi(() =>
        {
            last = data;
            IsBusy = false;
            reader.StopListening();
            Render(data);
        });

    private static void RunOnUi(Action action) => MainThread.BeginInvokeOnMainThread(action);

    // ---------------------------------------------------------------- rendering

    private void ClearResults()
    {
        Summary.Clear();
        Applications.Clear();
        Warnings.Clear();
        ApduTrace = null;
        IsApduVisible = false;
        HasResult = false;
        HasWarnings = false;
        HasApdu = false;
    }

    private void Render(EmvCardData d)
    {
        ClearResults();

        // ---- Summary ----
        AddSummary("Scheme", d.Scheme);
        AddSummary("Card number (PAN)", d.Pan);
        AddSummary("Expiry", d.Expiry);
        AddSummary("Effective", d.EffectiveDate);
        AddSummary("Cardholder", d.CardholderName);
        AddSummary("Application", d.ApplicationLabel);
        AddSummary("AID", d.Aid);
        AddSummary("PAN sequence", d.PanSequenceNumber);
        AddSummary("Service code", d.ServiceCode);
        AddSummary("Issuer country", d.IssuerCountry);
        AddSummary("Currency", d.Currency);
        AddSummary("Transaction counter (ATC)", d.Atc?.ToString());
        AddSummary("Last online ATC", d.LastOnlineAtc?.ToString());
        AddSummary("PIN tries left", d.PinTriesRemaining?.ToString());
        AddSummary("Card UID", d.Uid);
        AddSummary("Track 2", d.Track2);

        // ---- Per application: every tag ----
        foreach (var app in d.Applications)
        {
            var tags = app.Tags
                .Select(t => new TagRowView
                {
                    Header = $"[{t.Tag}] {t.Name}",
                    Decoded = t.Decoded,
                    ValueHex = t.ValueHex,
                    Source = t.Source,
                })
                .ToList();

            var transactions = app.Transactions
                .Select(tx => new TextLine("• " + tx))
                .ToList();

            Applications.Add(new CardApplicationView
            {
                SectionTitle = $"Data — {app.Label ?? app.Aid}",
                Header = $"{app.Label ?? app.PreferredName ?? "Application"}  ·  {app.Aid}",
                Tags = tags,
                Transactions = transactions,
            });
        }

        // ---- Warnings ----
        foreach (var warn in d.Warnings)
            Warnings.Add(new TextLine("• " + warn));
        HasWarnings = Warnings.Count > 0;

        // ---- Raw APDU trace ----
        if (d.ApduLog.Count > 0)
        {
            ApduTrace = string.Join("\n\n", d.ApduLog);
            HasApdu = true;
        }

        HasResult = true;
        Status = $"Read {d.AllTags.Count} data elements. Tap Scan for another card.";
    }

    private void AddSummary(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Summary.Add(new KeyValueRow(key, value));
    }

    private static string BuildTextDump(EmvCardData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CONTACTLESS CARD DUMP ===");
        void S(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"{k}: {v}"); }
        S("Scheme", d.Scheme);
        S("PAN", d.Pan);
        S("Expiry", d.Expiry);
        S("Effective", d.EffectiveDate);
        S("Cardholder", d.CardholderName);
        S("Application", d.ApplicationLabel);
        S("AID", d.Aid);
        S("PAN sequence", d.PanSequenceNumber);
        S("Service code", d.ServiceCode);
        S("Issuer country", d.IssuerCountry);
        S("Currency", d.Currency);
        S("ATC", d.Atc?.ToString());
        S("Last online ATC", d.LastOnlineAtc?.ToString());
        S("PIN tries left", d.PinTriesRemaining?.ToString());
        S("UID", d.Uid);
        S("Track2", d.Track2);

        foreach (var app in d.Applications)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Application {app.Label ?? app.Aid} ({app.Aid}) ---");
            foreach (var t in app.Tags)
                sb.AppendLine($"[{t.Tag}] {t.Name} = {t.Decoded ?? ""}  ({t.ValueHex})  <{t.Source}>");
            foreach (var tx in app.Transactions)
                sb.AppendLine($"  TX: {tx}");
        }

        if (d.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- Warnings ---");
            foreach (var w in d.Warnings)
                sb.AppendLine("• " + w);
        }

        sb.AppendLine();
        sb.AppendLine("--- Raw APDU trace ---");
        foreach (var line in d.ApduLog)
            sb.AppendLine(line);

        return sb.ToString();
    }
}
