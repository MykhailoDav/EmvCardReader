namespace EmvCardReader.Emv;

/// <summary>Platform-agnostic contactless EMV card reader surface used by the UI.</summary>
public interface INfcCardReader
{
    /// <summary>Whether NFC hardware is present and enabled.</summary>
    bool IsAvailable { get; }

    /// <summary>Human readable availability detail (e.g. "NFC disabled", "Not supported on this platform").</summary>
    string StatusText { get; }

    /// <summary>Begin listening for a card tap. Raises <see cref="CardRead"/> when a card is read.</summary>
    void StartListening();

    /// <summary>Stop listening.</summary>
    void StopListening();

    event EventHandler<EmvCardData> CardRead;
    event EventHandler<string> Status;
    event EventHandler<string> Error;
}
