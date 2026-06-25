using CoreNFC;
using Foundation;
using EmvCardReader.Emv;
using UIKit;

namespace EmvCardReader.Platforms.iOS;

public sealed class IosNfcCardReader : NSObject, INfcCardReader, INFCTagReaderSessionDelegate
{
    private NFCTagReaderSession? _session;

    public event EventHandler<EmvCardData>? CardRead;
    public event EventHandler<string>? Status;
    public event EventHandler<string>? Error;

    public bool IsAvailable => NFCReaderSession.ReadingAvailable;

    public string StatusText => IsAvailable
        ? "Ready — tap Scan, then hold a bank card to the top of the phone."
        : "NFC tag reading is not available on this device.";

    private static void Log(string msg) => Console.WriteLine($"[NFC] {msg}");

    public void StartListening()
    {
        Log($"StartListening: ReadingAvailable={NFCReaderSession.ReadingAvailable}");
        if (!NFCReaderSession.ReadingAvailable)
        {
            Error?.Invoke(this, "NFC tag reading is not available on this device.");
            return;
        }

        _session = new NFCTagReaderSession(NFCPollingOption.Iso14443, this, null)
        {
            AlertMessage = "Hold your bank card near the top of the phone.",
        };
        _session.BeginSession();
        Log("BeginSession called — polling ISO14443.");
        Status?.Invoke(this, "Hold your bank card near the top of the phone.");
    }

    public void StopListening()
    {
        Log("StopListening.");
        _session?.InvalidateSession();
        _session = null;
    }

    // ---- INFCTagReaderSessionDelegate ----

    [Export("tagReaderSessionDidBecomeActive:")]
    public void DidBecomeActive(NFCTagReaderSession session)
    {
        Log("DidBecomeActive — session is polling.");
        Status?.Invoke(this, "NFC active — hold the card flat to the top-back of the phone.");
    }

    [Export("tagReaderSession:didInvalidateWithError:")]
    public void DidInvalidate(NFCTagReaderSession session, NSError error)
    {
        var code = (NFCReaderError)(long)error.Code;
        Log($"DidInvalidate [{code}] raw={error.Code}: {error.LocalizedDescription}");
        if (code != NFCReaderError.ReaderSessionInvalidationErrorUserCanceled)
            Error?.Invoke(this, $"Session closed [{code}]: {error.LocalizedDescription}");
        _session = null;
    }

    [Export("tagReaderSession:didDetectTags:")]
    public void DidDetectTags(NFCTagReaderSession session, INFCTag[] tags)
    {
        Log($"DidDetectTags — {tags.Length} tag(s).");
        if (tags.Length == 0)
            return;

        Status?.Invoke(this, $"Card detected ({tags.Length}) — connecting…");

        var tag = tags[0];
        Log($"Tag[0] type={tag.Type}, iso7816={(tag.AsNFCIso7816Tag is null ? "no" : "yes")}");
        session.ConnectTo(tag, async connectError =>
        {
            if (connectError != null)
            {
                Log($"Connect failed: {connectError.LocalizedDescription}");
                session.InvalidateSession("Connection failed.");
                Error?.Invoke(this, $"Connect failed: {connectError.LocalizedDescription}");
                return;
            }

            Log("Connected to tag.");
            var iso = tag.AsNFCIso7816Tag;
            if (iso is null)
            {
                Log("AsNFCIso7816Tag is null — not an EMV card.");
                session.InvalidateSession("Not an ISO7816 card.");
                Error?.Invoke(this, "Tag is not an ISO7816 (EMV) card.");
                return;
            }

            try
            {
                Log("Reading EMV data…");
                Status?.Invoke(this, "Connected — reading EMV data…");

                var uid = iso.Identifier?.ToArray().ToHex();
                var tech = $"ISO7816, historical bytes: {iso.HistoricalBytes?.ToArray().ToHex() ?? "-"}";

                var transceiver = new Iso7816Transceiver(iso);
                var data = await EmvReader.ReadAsync(transceiver, uid, tech);

                Log($"Read complete — {data.AllTags.Count} data elements.");
                session.AlertMessage = "Card read complete ✓";
                session.InvalidateSession();
                CardRead?.Invoke(this, data);
            }
            catch (Exception ex)
            {
                Log($"Read failed: {ex}");
                session.InvalidateSession("Read failed.");
                Error?.Invoke(this, $"Read failed: {ex.Message}");
            }
        });
    }

    private sealed class Iso7816Transceiver(INFCIso7816Tag tag) : ICardTransceiver
    {
        private readonly INFCIso7816Tag _tag = tag;

        public Task<byte[]> TransceiveAsync(byte[] apdu)
        {
            var tcs = new TaskCompletionSource<byte[]>();
            var command = new NFCIso7816Apdu(NSData.FromArray(apdu));

            _tag.SendCommand(command, (response, sw1, sw2, error) =>
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
