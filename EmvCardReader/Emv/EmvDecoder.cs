using System.Text;

namespace EmvCardReader.Emv;

/// <summary>Turns raw EMV tag values into human-readable strings.</summary>
public static class EmvDecoder
{
    /// <summary>Best-effort decode of a primitive tag value into a friendly string (null if no special handling).</summary>
    public static string? Decode(string tag, byte[] value)
    {
        tag = tag.ToUpperInvariant();
        try
        {
            return tag switch
            {
                // PAN
                "5A" => FormatPan(Bcd(value)),
                // PAN sequence number
                "5F34" or "9F36" or "9F13" or "9F17" or "9F41" or "9F08" or "9F09" => Int(value).ToString(),
                // Expiration date
                "5F24" or "5F25" or "9A" => FormatDate(value),
                // Transaction time
                "9F21" => FormatTime(value),
                // Amount authorised
                "9F02" or "9F03" or "9F50" => FormatAmount(value),
                // Transaction currency
                "5F2A" or "9F42" or "9F51" => CurrencyName(value),
                // Issuer country
                "5F28" or "9F1A" or "9F57" => CountryName(value),
                // Service code
                "5F30" => Bcd(value),
                // Transaction type
                "9C" => TransactionType(value),
                // Application label
                "50" or "9F12" or "5F20" or "9F4E" => Ascii(value),
                // Language preference
                "5F2D" => Ascii(value),
                // Track 2 equivalent
                "57" => DecodeTrack2(value).Display,
                // Application priority indicator
                "87" => value.Length > 0 ? (value[0] & 0x0F).ToString() : null,
                // AIP
                "82" => DecodeAip(value),
                // SFI
                "88" => value.Length > 0 ? value[0].ToString() : null,
                // TTQ
                "9F66" or "9F6C" => value.ToHexSpaced(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    // ---------------- Track 2 ----------------

    public sealed record Track2(string? Pan, string? Expiry, string? ServiceCode, string Display);

    /// <summary>Parse Track 2 Equivalent Data (tag 57): PAN 'D' YYMM service-code discretionary.</summary>
    public static Track2 DecodeTrack2(byte[] value)
    {
        var sb = new StringBuilder();
        foreach (var b in value)
        {
            sb.Append((b >> 4).ToString("X"));
            sb.Append((b & 0x0F).ToString("X"));
        }
        var raw = sb.ToString();
        int sep = raw.IndexOfAny(new[] { 'D', 'd' });
        if (sep < 0)
            sep = raw.IndexOf('=');   // some encodings
        string? pan = null, expiry = null, service = null;
        if (sep > 0)
        {
            pan = raw[..sep];
            var rest = raw[(sep + 1)..].TrimEnd('F');
            if (rest.Length >= 4)
            {
                var yymm = rest[..4];
                expiry = $"20{yymm[..2]}-{yymm[2..4]}";
            }
            if (rest.Length >= 7)
                service = rest.Substring(4, 3);
        }
        var display = pan is null
            ? raw
            : $"PAN {FormatPan(pan)}" + (expiry != null ? $", exp {expiry}" : "") + (service != null ? $", svc {service}" : "");
        return new Track2(pan, expiry, service, display);
    }

    // ---------------- AIP bits ----------------

    private static string DecodeAip(byte[] value)
    {
        if (value.Length < 2)
            return value.ToHexSpaced();
        byte b1 = value[0];
        var caps = new List<string>();
        if ((b1 & 0x40) != 0) caps.Add("SDA");
        if ((b1 & 0x20) != 0) caps.Add("DDA");
        if ((b1 & 0x10) != 0) caps.Add("Cardholder verification");
        if ((b1 & 0x08) != 0) caps.Add("Terminal risk mgmt");
        if ((b1 & 0x04) != 0) caps.Add("Issuer authentication");
        if ((b1 & 0x01) != 0) caps.Add("CDA");
        return caps.Count > 0 ? string.Join(", ", caps) : value.ToHexSpaced();
    }

    // ---------------- primitives ----------------

    /// <summary>BCD digits as a string (e.g. {0x12,0x34} -> "1234"), trailing F padding stripped.</summary>
    public static string Bcd(byte[] value)
    {
        var sb = new StringBuilder(value.Length * 2);
        foreach (var b in value)
        {
            sb.Append((b >> 4).ToString("X"));
            sb.Append((b & 0x0F).ToString("X"));
        }
        return sb.ToString().TrimEnd('F');
    }

    public static long Int(byte[] value)
    {
        long n = 0;
        foreach (var b in value)
            n = (n << 8) | b;
        return n;
    }

    public static string Ascii(byte[] value)
    {
        var s = Encoding.ASCII.GetString(value).Trim();
        return new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }

    public static string FormatPan(string digits)
    {
        // group into 4s for readability
        var groups = new List<string>();
        for (int i = 0; i < digits.Length; i += 4)
            groups.Add(digits.Substring(i, Math.Min(4, digits.Length - i)));
        return string.Join(' ', groups);
    }

    private static string? FormatDate(byte[] value)
    {
        var d = Bcd(value).PadLeft(6, '0');
        if (d.Length < 6)
            return d;
        return $"20{d[..2]}-{d[2..4]}-{d[4..6]}";
    }

    private static string? FormatTime(byte[] value)
    {
        var t = Bcd(value).PadLeft(6, '0');
        if (t.Length < 6)
            return t;
        return $"{t[..2]}:{t[2..4]}:{t[4..6]}";
    }

    private static string FormatAmount(byte[] value)
    {
        // n12: 6-byte BCD amount with 2 implied decimals
        var digits = Bcd(value);
        if (digits.Length == 0)
            digits = "0";
        if (!long.TryParse(digits, out var cents))
            return digits;
        return (cents / 100m).ToString("0.00");
    }

    private static string TransactionType(byte[] value)
    {
        if (value.Length == 0)
            return "?";
        return value[0] switch
        {
            0x00 => "Purchase",
            0x01 => "Cash advance",
            0x09 => "Purchase with cashback",
            0x20 => "Refund",
            0x30 => "Balance inquiry",
            _ => $"0x{value[0]:X2}",
        };
    }

    // ---------------- ISO lookups ----------------

    public static string CurrencyName(byte[] value)
    {
        var code = NormalizeCode(Bcd(value));
        return Currencies.TryGetValue(code, out var name) ? $"{name} ({code})" : code;
    }

    public static string CountryName(byte[] value)
    {
        var code = NormalizeCode(Bcd(value));
        return Countries.TryGetValue(code, out var name) ? $"{name} ({code})" : code;
    }

    // ISO numeric codes are n3 stored in 2 BCD bytes (e.g. "0804"); strip to 3 digits.
    private static string NormalizeCode(string bcd)
        => int.TryParse(bcd, out var n) ? n.ToString("D3") : bcd.PadLeft(3, '0');

    private static readonly Dictionary<string, string> Currencies = new()
    {
        ["840"] = "USD", ["978"] = "EUR", ["826"] = "GBP", ["980"] = "UAH",
        ["985"] = "PLN", ["756"] = "CHF", ["392"] = "JPY", ["156"] = "CNY",
        ["124"] = "CAD", ["036"] = "AUD", ["643"] = "RUB", ["949"] = "TRY",
        ["203"] = "CZK", ["348"] = "HUF", ["946"] = "RON", ["208"] = "DKK",
        ["752"] = "SEK", ["578"] = "NOK", ["356"] = "INR", ["410"] = "KRW",
        ["784"] = "AED", ["682"] = "SAR", ["376"] = "ILS", ["986"] = "BRL",
        ["484"] = "MXN", ["344"] = "HKD", ["702"] = "SGD", ["764"] = "THB",
    };

    private static readonly Dictionary<string, string> Countries = new()
    {
        ["840"] = "United States", ["826"] = "United Kingdom", ["804"] = "Ukraine",
        ["616"] = "Poland", ["276"] = "Germany", ["250"] = "France", ["380"] = "Italy",
        ["724"] = "Spain", ["528"] = "Netherlands", ["056"] = "Belgium", ["756"] = "Switzerland",
        ["040"] = "Austria", ["372"] = "Ireland", ["620"] = "Portugal", ["300"] = "Greece",
        ["203"] = "Czechia", ["348"] = "Hungary", ["642"] = "Romania", ["208"] = "Denmark",
        ["752"] = "Sweden", ["578"] = "Norway", ["246"] = "Finland", ["124"] = "Canada",
        ["036"] = "Australia", ["392"] = "Japan", ["156"] = "China", ["356"] = "India",
        ["410"] = "South Korea", ["643"] = "Russia", ["792"] = "Turkey", ["784"] = "UAE",
        ["682"] = "Saudi Arabia", ["376"] = "Israel", ["076"] = "Brazil", ["484"] = "Mexico",
        ["344"] = "Hong Kong", ["702"] = "Singapore", ["764"] = "Thailand",
    };
}
