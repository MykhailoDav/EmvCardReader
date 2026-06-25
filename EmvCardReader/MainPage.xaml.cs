using System.Text;
using EmvCardReader.Emv;

namespace EmvCardReader;

public partial class MainPage : ContentPage
{
	private readonly INfcCardReader? _reader;
	private EmvCardData? _last;

	public MainPage()
	{
		InitializeComponent();

		_reader = ServiceHelper.GetService<INfcCardReader>();
		if (_reader is null)
		{
			StatusLabel.Text = "NFC is not supported on this platform.";
			ScanButton.IsEnabled = false;
			return;
		}

		_reader.CardRead += OnCardRead;
		_reader.Status += OnStatus;
		_reader.Error += OnError;

		StatusLabel.Text = _reader.StatusText;
		ScanButton.IsEnabled = _reader.IsAvailable;
	}

	private void OnScanClicked(object? sender, EventArgs e)
	{
		if (_reader is null)
			return;

		ResultsLayout.Children.Clear();
		SetBusy(true);
		StatusLabel.Text = "Waiting for card…";
		_reader.StartListening();
	}

	private void OnStatus(object? sender, string message)
		=> MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);

	private void OnError(object? sender, string message)
		=> MainThread.BeginInvokeOnMainThread(() =>
		{
			SetBusy(false);
			StatusLabel.Text = $"⚠️ {message}";
		});

	private void OnCardRead(object? sender, EmvCardData data)
		=> MainThread.BeginInvokeOnMainThread(() =>
		{
			_last = data;
			SetBusy(false);
			_reader?.StopListening();
			RenderCard(data);
		});

	private void SetBusy(bool busy)
	{
		Spinner.IsRunning = busy;
		Spinner.IsVisible = busy;
		ScanButton.IsEnabled = !busy && (_reader?.IsAvailable ?? false);
	}

	// ---------------------------------------------------------------- rendering

	private void RenderCard(EmvCardData d)
	{
		ResultsLayout.Children.Clear();

		// Copy-all button
		var copy = new Button
		{
			Text = "📋 Copy everything as text",
			CornerRadius = 10,
			FontSize = 14,
		};
		copy.Clicked += async (_, _) =>
		{
			await Clipboard.SetTextAsync(BuildTextDump(d));
			StatusLabel.Text = "Copied to clipboard.";
		};
		ResultsLayout.Children.Add(copy);

		// ---- Summary ----
		var summary = new VerticalStackLayout { Spacing = 6 };
		AddRow(summary, "Scheme", d.Scheme);
		AddRow(summary, "Card number (PAN)", d.Pan);
		AddRow(summary, "Expiry", d.Expiry);
		AddRow(summary, "Effective", d.EffectiveDate);
		AddRow(summary, "Cardholder", d.CardholderName);
		AddRow(summary, "Application", d.ApplicationLabel);
		AddRow(summary, "AID", d.Aid);
		AddRow(summary, "PAN sequence", d.PanSequenceNumber);
		AddRow(summary, "Service code", d.ServiceCode);
		AddRow(summary, "Issuer country", d.IssuerCountry);
		AddRow(summary, "Currency", d.Currency);
		AddRow(summary, "Transaction counter (ATC)", d.Atc?.ToString());
		AddRow(summary, "Last online ATC", d.LastOnlineAtc?.ToString());
		AddRow(summary, "PIN tries left", d.PinTriesRemaining?.ToString());
		AddRow(summary, "Card UID", d.Uid);
		AddRow(summary, "Track 2", d.Track2);
		ResultsLayout.Children.Add(MakeSection("Summary", summary));

		// ---- Per application: every tag ----
		foreach (var app in d.Applications)
		{
			var tagsLayout = new VerticalStackLayout { Spacing = 8 };

			var header = new Label
			{
				Text = $"{app.Label ?? app.PreferredName ?? "Application"}  ·  {app.Aid}",
				FontAttributes = FontAttributes.Bold,
				FontSize = 13,
			};
			tagsLayout.Children.Add(header);

			foreach (var tag in app.Tags)
				tagsLayout.Children.Add(MakeTagRow(tag));

			if (app.Transactions.Count > 0)
			{
				tagsLayout.Children.Add(new Label
				{
					Text = $"Transaction log ({app.Transactions.Count})",
					FontAttributes = FontAttributes.Bold,
					FontSize = 13,
					Margin = new Thickness(0, 8, 0, 0),
				});
				foreach (var tx in app.Transactions)
					tagsLayout.Children.Add(new Label { Text = "• " + tx, FontSize = 12 });
			}

			ResultsLayout.Children.Add(MakeSection($"Data — {app.Label ?? app.Aid}", tagsLayout));
		}

		// ---- Warnings ----
		if (d.Warnings.Count > 0)
		{
			var w = new VerticalStackLayout { Spacing = 4 };
			foreach (var warn in d.Warnings)
				w.Children.Add(new Label { Text = "• " + warn, FontSize = 12, TextColor = Colors.OrangeRed });
			ResultsLayout.Children.Add(MakeSection("Warnings", w));
		}

		// ---- Raw APDU trace (collapsible) ----
		if (d.ApduLog.Count > 0)
		{
			var logBody = new Label
			{
				Text = string.Join("\n\n", d.ApduLog),
				FontSize = 11,
				IsVisible = false,
			};
			var toggle = new Button { Text = "Show raw APDU trace", FontSize = 13, CornerRadius = 10 };
			toggle.Clicked += (_, _) =>
			{
				logBody.IsVisible = !logBody.IsVisible;
				toggle.Text = logBody.IsVisible ? "Hide raw APDU trace" : "Show raw APDU trace";
			};
			var logLayout = new VerticalStackLayout { Spacing = 8 };
			logLayout.Children.Add(toggle);
			logLayout.Children.Add(logBody);
			ResultsLayout.Children.Add(MakeSection("Raw APDU trace", logLayout));
		}

		StatusLabel.Text = $"Read {d.AllTags.Count} data elements. Tap Scan for another card.";
	}

	private static Border MakeSection(string title, View content)
	{
		var stack = new VerticalStackLayout { Spacing = 8 };
		stack.Children.Add(new Label { Text = title, FontAttributes = FontAttributes.Bold, FontSize = 16 });
		stack.Children.Add(content);

		return new Border
		{
			StrokeThickness = 0,
			BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#1C1C1E") : Colors.White,
			Padding = 16,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
			Content = stack,
		};
	}

	private static void AddRow(Layout parent, string key, string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return;
		parent.Children.Add(MakeRow(key, value!));
	}

	private static View MakeRow(string key, string value)
	{
		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) },
				new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) },
			},
			ColumnSpacing = 8,
		};
		var k = new Label { Text = key, FontSize = 13, TextColor = Color.FromArgb("#8E8E93") };
		var v = new Label { Text = value, FontSize = 13, FontAttributes = FontAttributes.Bold };
		grid.Add(k, 0, 0);
		grid.Add(v, 1, 0);
		return grid;
	}

	private static View MakeTagRow(EmvTagValue tag)
	{
		var stack = new VerticalStackLayout { Spacing = 1 };
		stack.Children.Add(new Label
		{
			Text = $"[{tag.Tag}] {tag.Name}",
			FontSize = 12,
			FontAttributes = FontAttributes.Bold,
		});
		if (!string.IsNullOrEmpty(tag.Decoded))
			stack.Children.Add(new Label { Text = tag.Decoded, FontSize = 13 });
		stack.Children.Add(new Label
		{
			Text = tag.ValueHex,
			FontSize = 10,
			TextColor = Color.FromArgb("#8E8E93"),
		});
		stack.Children.Add(new Label
		{
			Text = tag.Source,
			FontSize = 9,
			TextColor = Color.FromArgb("#C7C7CC"),
		});
		return stack;
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
