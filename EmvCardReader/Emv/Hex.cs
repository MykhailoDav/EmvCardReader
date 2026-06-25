namespace EmvCardReader.Emv;

/// <summary>Helpers for converting between byte arrays and hex strings.</summary>
public static class Hex
{
    public static string ToHex(this byte[]? data)
        => data is null ? string.Empty : Convert.ToHexString(data);

    public static string ToHex(this ReadOnlySpan<byte> data)
        => Convert.ToHexString(data);

    /// <summary>Pretty hex with spaces between bytes, e.g. "6F 1A 84 ...".</summary>
    public static string ToHexSpaced(this byte[]? data)
    {
        if (data is null || data.Length == 0)
            return string.Empty;
        return string.Join(' ', data.Select(b => b.ToString("X2")));
    }

    public static byte[] FromHex(string hex)
    {
        hex = hex.Replace(" ", string.Empty).Replace("-", string.Empty);
        return Convert.FromHexString(hex);
    }
}
