namespace EmvCardReader.Services;

/// <summary>Default <see cref="IClipboardService"/> backed by MAUI's <see cref="Clipboard"/>.</summary>
public sealed class ClipboardService : IClipboardService
{
    public Task SetTextAsync(string? text) => Clipboard.Default.SetTextAsync(text);
}
