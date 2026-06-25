namespace EmvCardReader.Emv;

/// <summary>
/// Platform-agnostic raw APDU channel to an ISO 14443-4 / ISO 7816 card.
/// The returned bytes MUST include the trailing 2-byte status word (SW1 SW2),
/// matching Android's IsoDep.Transceive behaviour. iOS implementations append
/// SW1/SW2 to the response data so both platforms look identical to the EMV layer.
/// </summary>
public interface ICardTransceiver
{
    Task<byte[]> TransceiveAsync(byte[] apdu);
}

/// <summary>Parsed APDU response: data payload + status word.</summary>
public sealed class ApduResponse
{
    public required byte[] Data { get; init; }   // response WITHOUT SW1 SW2
    public byte Sw1 { get; init; }
    public byte Sw2 { get; init; }

    public int StatusWord => (Sw1 << 8) | Sw2;
    public bool IsSuccess => StatusWord == 0x9000;
    public string StatusHex => $"{Sw1:X2}{Sw2:X2}";

    public static ApduResponse FromRaw(byte[] raw)
    {
        if (raw.Length < 2)
            return new ApduResponse { Data = Array.Empty<byte>(), Sw1 = 0, Sw2 = 0 };
        var data = new byte[raw.Length - 2];
        Array.Copy(raw, data, data.Length);
        return new ApduResponse
        {
            Data = data,
            Sw1 = raw[^2],
            Sw2 = raw[^1],
        };
    }
}
