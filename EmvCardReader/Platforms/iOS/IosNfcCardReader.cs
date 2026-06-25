using CoreNFC;
using Foundation;
using EmvCardReader.Emv;
using UIKit;

namespace EmvCardReader.Platforms.iOS;

/// <summary>
/// iOS EMV reader built on CoreNFC's NFCTagReaderSession (ISO 14443) and ISO7816 APDUs.
/// Requires the "Near Field Communication Tag Reading" capability, the NFC entitlement,
/// NFCReaderUsageDescription in Info.plist, and the card AIDs declared under
/// com.apple.developer.nfc.readersession.iso7816.select-identifiers.
/// </summary>
public sealed class IosNfcCardReader : NSObject, INfcCardReader, INFCTagReaderSessionDelegate
{
    private NFCTagReaderSession? _session;

    public event EventHandler<EmvCardData>? CardRead;
    public event EventHandler<string>? Status;
    public event EventHandler<string>? Error;

    public bool IsAvailable => NFCTagReaderSession.ReadingAvailable;

    public string StatusText => IsAvailable
        ? "Ready — tap Scan, then hold a bank card to the top of the phone."
        : "NFC tag reading is not available on this device.";

    public void StartListening()
    {
        if (!NFCTagReaderSession.ReadingAvailable)
        {
            Error?.Invoke(this, "NFC tag reading is not available on this device.");
            return;
        }

        _session = new NFCTagReaderSession(NFCPollingOption.Iso14443, this, null)
        {
            AlertMessage = "Hold your bank card near the top of the phone.",
        };
        _session.BeginSession();
        Status?.Invoke(this, "Hold your bank card near the top of the phone.");
    }

    public void StopListening()
    {
        _session?.InvalidateSession();
        _session = null;
    }

    // ---- INFCTagReaderSessionDelegate ----

    [Export("tagReaderSessionDidBecomeActive:")]
    public void DidBecomeActive(NFCTagReaderSession session)
    {
        // Session is now polling.
    }

    [Export("tagReaderSession:didInvalidateWithError:")]
    public void DidInvalidate(NFCTagReaderSession session, NSError error)
    {
        // User cancel / timeout / etc. NFCReaderError.ReaderSessionInvalidationErrorUserCanceled is benign.
        var code = (NFCReaderError)(long)error.Code;
        if (code != NFCReaderError.ReaderSessionInvalidationErrorUserCanceled)
            Error?.Invoke(this, error.LocalizedDescription);
        _session = null;
    }

    [Export("tagReaderSession:didDetectTags:")]
    public void DidDetectTags(NFCTagReaderSession session, INFCTag[] tags)
    {
        if (tags.Length == 0)
            return;

        var tag = tags[0];
        session.ConnectTo(tag, async (NSError connectError) =>
        {
            if (connectError != null)
            {
                session.InvalidateSession("Connection failed.");
                Error?.Invoke(this, $"Connect failed: {connectError.LocalizedDescription}");
                return;
            }

            var iso = tag.AsNFCIso7816Tag;
            if (iso is null)
            {
                session.InvalidateSession("Not an ISO7816 card.");
                Error?.Invoke(this, "Tag is not an ISO7816 (EMV) card.");
                return;
            }

            try
            {
                var uid = iso.Identifier?.ToArray().ToHex();
                var tech = $"ISO7816, historical bytes: {iso.HistoricalBytes?.ToArray().ToHex() ?? "-"}";

                var transceiver = new Iso7816Transceiver(iso);
                var data = await EmvReader.ReadAsync(transceiver, uid, tech);

                session.AlertMessage = "Card read complete ✓";
                session.InvalidateSession();
                CardRead?.Invoke(this, data);
            }
            catch (Exception ex)
            {
                session.InvalidateSession("Read failed.");
                Error?.Invoke(this, $"Read failed: {ex.Message}");
            }
        });
    }

    /// <summary>Adapts CoreNFC's completion-handler SendCommand to the async transceiver,
    /// appending SW1/SW2 to the response so it matches Android's IsoDep format.</summary>
    private sealed class Iso7816Transceiver : ICardTransceiver
    {
        private readonly INFCIso7816Tag _tag;
        public Iso7816Transceiver(INFCIso7816Tag tag) => _tag = tag;

        public Task<byte[]> TransceiveAsync(byte[] apdu)
        {
            var tcs = new TaskCompletionSource<byte[]>();
            var command = new NFCIso7816Apdu(NSData.FromArray(apdu));

            _tag.SendCommand(command, (NSData response, byte sw1, byte sw2, NSError? error) =>
            {
                if (error != null)
                {
                    tcs.TrySetException(new Exception(error.LocalizedDescription));
                    return;
                }
                var payload = response?.ToArray() ?? Array.Empty<byte>();
                var full = new byte[payload.Length + 2];
                Array.Copy(payload, full, payload.Length);
                full[^2] = sw1;
                full[^1] = sw2;
                tcs.TrySetResult(full);
            });

            return tcs.Task;
        }
    }
}
