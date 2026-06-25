namespace EmvCardReader.Services;

/// <summary>Abstraction over the platform clipboard so view models stay testable.</summary>
public interface IClipboardService
{
    Task SetTextAsync(string? text);
}
