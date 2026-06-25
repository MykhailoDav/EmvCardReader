using Android.App;
using Android.Nfc;
using Android.Nfc.Tech;
using Android.OS;
using EmvCardReader.Emv;
using Application = Android.App.Application;

namespace EmvCardReader.Platforms.Android;

/// <summary>
/// Android EMV reader. Uses NfcAdapter reader-mode so the OS hands us the tag directly
/// (no NDEF discovery), then talks raw APDUs to it over IsoDep.
/// </summary>
public sealed class AndroidNfcCardReader : Java.Lang.Object, INfcCardReader, NfcAdapter.IReaderCallback
{
    private NfcAdapter? _adapter;
    private bool _listening;

    public event EventHandler<EmvCardData>? CardRead;
    public event EventHandler<string>? Status;
    public event EventHandler<string>? Error;

    private NfcAdapter? Adapter
    {
        get
        {
            if (_adapter != null)
                return _adapter;
            global::Android.Content.Context? ctx = Platform.CurrentActivity ?? Application.Context;
            _adapter = ctx != null ? NfcAdapter.GetDefaultAdapter(ctx) : null;
            return _adapter;
        }
    }

    public bool IsAvailable => Adapter is { IsEnabled: true };

    public string StatusText
    {
        get
        {
            if (Adapter is null) return "This device has no NFC hardware.";
            if (!Adapter.IsEnabled) return "NFC is turned off — enable it in system settings.";
            return "Ready — hold a bank card to the back of the phone.";
        }
    }

    public void StartListening()
    {
        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            Error?.Invoke(this, "No foreground activity to attach NFC reader to.");
            return;
        }
        if (Adapter is null)
        {
            Error?.Invoke(this, "This device has no NFC hardware.");
            return;
        }
        if (!Adapter.IsEnabled)
        {
            Error?.Invoke(this, "NFC is turned off — enable it in system settings.");
            return;
        }

        // Skip NDEF checks and silence platform sounds; poll A and B (covers all EMV cards).
        var flags = NfcReaderFlags.NfcA | NfcReaderFlags.NfcB
                    | NfcReaderFlags.SkipNdefCheck | NfcReaderFlags.NoPlatformSounds;

        // Give slow cards extra time to answer APDUs.
        var extras = new Bundle();
        extras.PutInt(NfcAdapter.ExtraReaderPresenceCheckDelay, 1000);

        Adapter.EnableReaderMode(activity, this, flags, extras);
        _listening = true;
        Status?.Invoke(this, "Ready — hold a bank card to the back of the phone.");
    }

    public void StopListening()
    {
        if (!_listening)
            return;
        var activity = Platform.CurrentActivity;
        if (activity != null && Adapter != null)
            Adapter.DisableReaderMode(activity);
        _listening = false;
    }

    // Called by the OS on a background thread when a tag enters the field.
    public async void OnTagDiscovered(global::Android.Nfc.Tag? tag)
    {
        if (tag is null)
            return;

        var isoDep = IsoDep.Get(tag);
        if (isoDep is null)
        {
            Error?.Invoke(this, "Tag is not ISO-DEP (not an EMV card?).");
            return;
        }

        try
        {
            Status?.Invoke(this, "Card detected — reading…");
            isoDep.Connect();
            isoDep.Timeout = 5000;

            var uid = tag.GetId()?.ToHex();
            var tech = $"IsoDep, maxTransceive={isoDep.MaxTransceiveLength} bytes, " +
                       $"historical/hi-layer present";

            var transceiver = new IsoDepTransceiver(isoDep);
            var data = await EmvReader.ReadAsync(transceiver, uid, tech);

            CardRead?.Invoke(this, data);
            Status?.Invoke(this, "Done. Tap another card or stop.");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Read failed: {ex.Message}");
        }
        finally
        {
            try { isoDep.Close(); } catch { /* ignore */ }
        }
    }

    /// <summary>Wraps Android's blocking IsoDep.Transceive as an async transceiver.</summary>
    private sealed class IsoDepTransceiver : ICardTransceiver
    {
        private readonly IsoDep _isoDep;
        public IsoDepTransceiver(IsoDep isoDep) => _isoDep = isoDep;

        public Task<byte[]> TransceiveAsync(byte[] apdu)
        {
            // Already invoked on a background thread by OnTagDiscovered.
            var resp = _isoDep.Transceive(apdu) ?? Array.Empty<byte>();
            return Task.FromResult(resp);
        }
    }
}
