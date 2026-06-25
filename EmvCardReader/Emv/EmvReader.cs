using System.Text;

namespace EmvCardReader.Emv;

/// <summary>
/// Drives the EMV contactless read flow against an <see cref="ICardTransceiver"/> and
/// extracts the maximum amount of publicly readable data:
///   SELECT PPSE -> SELECT AID(s) -> GET PROCESSING OPTIONS -> READ RECORD (AFL)
///   -> GET DATA (ATC, last online ATC, PIN tries, log format) -> read transaction log.
/// No authentication is performed; only data the card exposes to a terminal.
/// </summary>
public sealed class EmvReader
{
    private readonly ICardTransceiver _t;
    private readonly EmvCardData _result = new();

    private EmvReader(ICardTransceiver t) => _t = t;

    public static Task<EmvCardData> ReadAsync(ICardTransceiver transceiver, string? uid = null, string? tech = null)
    {
        var reader = new EmvReader(transceiver);
        reader._result.Uid = uid;
        reader._result.TechInfo = tech;
        return reader.RunAsync();
    }

    private async Task<EmvCardData> RunAsync()
    {
        try
        {
            var aids = await SelectPpseAsync();
            if (aids.Count == 0)
            {
                _result.Warnings.Add("PPSE returned no applications – falling back to a known AID list.");
                aids = await DiscoverByKnownAidsAsync();
            }

            if (aids.Count == 0)
            {
                _result.Warnings.Add("No EMV application could be selected. Is this an EMV payment card?");
                return _result;
            }

            foreach (var candidate in aids)
            {
                try
                {
                    await ReadApplicationAsync(candidate);
                }
                catch (Exception ex)
                {
                    _result.Warnings.Add($"Failed to read AID {candidate.Aid}: {ex.Message}");
                }
            }

            PopulateConvenienceFields();
        }
        catch (Exception ex)
        {
            _result.Warnings.Add($"Read aborted: {ex.Message}");
        }
        return _result;
    }

    // ---------------------------------------------------------------- PPSE

    private async Task<List<AidCandidate>> SelectPpseAsync()
    {
        var ppse = Encoding.ASCII.GetBytes("2PAY.SYS.DDF01");
        var resp = await SelectByNameAsync(ppse, "SELECT PPSE");
        var list = new List<AidCandidate>();
        if (resp is null || !resp.IsSuccess)
            return list;

        foreach (var node in TlvParser.Parse(resp.Data).SelectMany(n => n.Flatten()))
        {
            // Directory entries (61) hold 4F (AID), 50 (label), 87 (priority)
            if (node.Tag == "61")
            {
                string? aid = null, label = null;
                int? prio = null;
                foreach (var c in node.Children)
                {
                    if (c.Tag == "4F") aid = c.Value.ToHex();
                    else if (c.Tag == "50") label = EmvDecoder.Ascii(c.Value);
                    else if (c.Tag == "87" && c.Value.Length > 0) prio = c.Value[0];
                }
                if (aid != null)
                    list.Add(new AidCandidate(aid, label, prio));
            }
        }

        // Fallback: a stray 4F not wrapped in 61
        if (list.Count == 0)
        {
            foreach (var node in TlvParser.Parse(resp.Data).SelectMany(n => n.Flatten()))
                if (node.Tag == "4F")
                    list.Add(new AidCandidate(node.Value.ToHex(), null, null));
        }

        return list
            .GroupBy(a => a.Aid)
            .Select(g => g.First())
            .OrderBy(a => a.Priority ?? 99)
            .ToList();
    }

    private async Task<List<AidCandidate>> DiscoverByKnownAidsAsync()
    {
        var found = new List<AidCandidate>();
        foreach (var aid in KnownAids)
        {
            var resp = await SelectByNameAsync(Hex.FromHex(aid), $"SELECT AID {aid}");
            if (resp is { IsSuccess: true })
                found.Add(new AidCandidate(aid, null, null));
        }
        return found;
    }

    // ---------------------------------------------------------------- per application

    private async Task ReadApplicationAsync(AidCandidate candidate)
    {
        var aidBytes = Hex.FromHex(candidate.Aid);
        var selectResp = await SelectByNameAsync(aidBytes, $"SELECT AID {candidate.Aid}");
        if (selectResp is null || !selectResp.IsSuccess)
        {
            _result.Warnings.Add($"SELECT AID {candidate.Aid} failed (SW {selectResp?.StatusHex}).");
            return;
        }

        var app = new EmvApplication
        {
            Aid = candidate.Aid,
            Label = candidate.Label,
            Priority = candidate.Priority,
        };
        _result.Applications.Add(app);

        var fciNodes = TlvParser.Parse(selectResp.Data);
        RecordTags(fciNodes, app, $"SELECT AID {candidate.Aid}");

        byte[]? pdol = FindValue(fciNodes, "9F38");
        var prefName = FindValue(fciNodes, "9F12");
        if (prefName != null)
            app.PreferredName = EmvDecoder.Ascii(prefName);
        var labelBytes = FindValue(fciNodes, "50");
        if (labelBytes != null)
            app.Label ??= EmvDecoder.Ascii(labelBytes);

        // ---- GET PROCESSING OPTIONS ----
        var gpoResp = await GetProcessingOptionsAsync(pdol);
        byte[]? afl = null;
        if (gpoResp is { IsSuccess: true })
        {
            (var aip, afl) = ParseGpo(gpoResp.Data, app);
        }
        else
        {
            _result.Warnings.Add($"GET PROCESSING OPTIONS failed (SW {gpoResp?.StatusHex}). Retrying records via brute-force scan.");
        }

        // ---- READ RECORD over the AFL (or brute force if no AFL) ----
        if (afl != null && afl.Length >= 4)
            await ReadAflRecordsAsync(afl, app);
        else
            await BruteForceRecordsAsync(app);

        // ---- GET DATA for standalone counters ----
        await ReadGetDataAsync(app);

        // ---- Transaction log ----
        await ReadTransactionLogAsync(fciNodes, app);
    }

    // ---------------------------------------------------------------- GPO

    private async Task<ApduResponse?> GetProcessingOptionsAsync(byte[]? pdol)
    {
        byte[] pdolData = pdol != null ? BuildPdol(pdol) : Array.Empty<byte>();
        // Command data = 83 <len> <pdol data>
        var cmdData = new byte[2 + pdolData.Length];
        cmdData[0] = 0x83;
        cmdData[1] = (byte)pdolData.Length;
        Array.Copy(pdolData, 0, cmdData, 2, pdolData.Length);

        var apdu = BuildApdu(0x80, 0xA8, 0x00, 0x00, cmdData, le: 0x00);
        return await SendAsync(apdu, "GET PROCESSING OPTIONS");
    }

    /// <summary>Build PDOL response data from the card's PDOL tag list using sane terminal defaults.</summary>
    private byte[] BuildPdol(byte[] pdol)
    {
        var dol = TlvParser.ParseDol(pdol);
        var ms = new MemoryStream();
        foreach (var (tag, len) in dol)
        {
            var value = DefaultTerminalValue(tag, len);
            ms.Write(value, 0, value.Length);
        }
        return ms.ToArray();
    }

    private byte[] DefaultTerminalValue(string tag, int len)
    {
        byte[] v = tag.ToUpperInvariant() switch
        {
            "9F66" => new byte[] { 0x36, 0x00, 0x00, 0x00 },               // TTQ: EMV + contact ok
            "9F02" => new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 },   // amount 0.01
            "9F03" => new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },   // amount other
            "9F1A" => new byte[] { 0x08, 0x40 },                           // terminal country = 840 (US)
            "5F2A" => new byte[] { 0x08, 0x40 },                           // currency = 840 (USD)
            "95" => new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 },           // TVR
            "9A" => DateYymmdd(),                                          // transaction date
            "9C" => new byte[] { 0x00 },                                   // purchase
            "9F37" => RandomBytes(4),                                      // unpredictable number
            "9F35" => new byte[] { 0x22 },                                 // terminal type = attended, offline
            "9F40" => new byte[] { 0xF0, 0x00, 0xF0, 0xA0, 0x01 },         // additional terminal caps
            "9F33" => new byte[] { 0xE0, 0xF8, 0xC8 },                     // terminal capabilities
            "9F4E" => new byte[len],                                       // merchant name/location
            "9F1D" => new byte[len],
            "9F15" => new byte[] { 0x00, 0x00 },                           // MCC
            _ => new byte[len],
        };

        if (v.Length == len)
            return v;
        // Normalise to requested length (right-align numerics, pad with zeros).
        var outBuf = new byte[len];
        int copy = Math.Min(len, v.Length);
        Array.Copy(v, v.Length - copy, outBuf, len - copy, copy);
        return outBuf;
    }

    private (byte[]? aip, byte[]? afl) ParseGpo(byte[] data, EmvApplication app)
    {
        byte[]? aip = null, afl = null;
        var nodes = TlvParser.Parse(data);

        var template80 = nodes.FirstOrDefault(n => n.Tag == "80");
        if (template80 != null)
        {
            // Format 1: AIP (2 bytes) || AFL (rest)
            var v = template80.Value;
            if (v.Length >= 2)
            {
                aip = v[..2];
                afl = v[2..];
                AddTag(app, "82", aip, "GPO (format 1)");
                AddTag(app, "94", afl, "GPO (format 1)");
            }
        }
        else
        {
            // Format 2: template 77 with nested 82 / 94
            RecordTags(nodes, app, "GPO (format 2)");
            aip = FindValue(nodes, "82");
            afl = FindValue(nodes, "94");
        }
        return (aip, afl);
    }

    // ---------------------------------------------------------------- READ RECORD

    private async Task ReadAflRecordsAsync(byte[] afl, EmvApplication app)
    {
        for (int i = 0; i + 4 <= afl.Length; i += 4)
        {
            int sfi = afl[i] >> 3;
            int first = afl[i + 1];
            int last = afl[i + 2];
            if (sfi == 0 || first == 0 || last < first)
                continue;

            for (int rec = first; rec <= last; rec++)
            {
                var resp = await ReadRecordAsync(sfi, rec);
                if (resp is { IsSuccess: true } && resp.Data.Length > 0)
                {
                    var nodes = TlvParser.Parse(resp.Data);
                    RecordTags(nodes, app, $"Record SFI {sfi} #{rec}");
                }
            }
        }
    }

    /// <summary>When no AFL is available, scan the first few SFIs/records.</summary>
    private async Task BruteForceRecordsAsync(EmvApplication app)
    {
        for (int sfi = 1; sfi <= 10; sfi++)
        {
            int consecutiveMisses = 0;
            for (int rec = 1; rec <= 16; rec++)
            {
                var resp = await ReadRecordAsync(sfi, rec);
                if (resp is { IsSuccess: true } && resp.Data.Length > 0)
                {
                    consecutiveMisses = 0;
                    var nodes = TlvParser.Parse(resp.Data);
                    RecordTags(nodes, app, $"Record SFI {sfi} #{rec} (scan)");
                }
                else if (++consecutiveMisses >= 2)
                {
                    break; // stop scanning this SFI after 2 misses in a row
                }
            }
        }
    }

    private Task<ApduResponse?> ReadRecordAsync(int sfi, int record)
    {
        byte p2 = (byte)((sfi << 3) | 0x04);
        var apdu = BuildApdu(0x00, 0xB2, (byte)record, p2, null, le: 0x00);
        return SendAsync(apdu, $"READ RECORD SFI {sfi} #{record}", logFailures: false);
    }

    // ---------------------------------------------------------------- GET DATA

    private async Task ReadGetDataAsync(EmvApplication app)
    {
        var dataTags = new (byte hi, byte lo, string tag)[]
        {
            (0x9F, 0x36, "9F36"), // ATC
            (0x9F, 0x13, "9F13"), // Last online ATC
            (0x9F, 0x17, "9F17"), // PIN try counter
            (0x9F, 0x4F, "9F4F"), // Log format
            (0x9F, 0x50, "9F50"), // Offline accumulator balance
        };

        foreach (var (hi, lo, tag) in dataTags)
        {
            var apdu = BuildApdu(0x80, 0xCA, hi, lo, null, le: 0x00);
            var resp = await SendAsync(apdu, $"GET DATA {tag}", logFailures: false);
            if (resp is { IsSuccess: true } && resp.Data.Length > 0)
            {
                var nodes = TlvParser.Parse(resp.Data);
                RecordTags(nodes, app, "GET DATA");
            }
        }
    }

    // ---------------------------------------------------------------- transaction log

    private async Task ReadTransactionLogAsync(List<TlvNode> fciNodes, EmvApplication app)
    {
        // Log Entry (9F4D): byte0 = SFI, byte1 = number of records.
        byte[]? logEntry = FindValue(fciNodes, "9F4D");
        if (logEntry is null)
        {
            var te = app.Tags.FirstOrDefault(t => t.Tag == "9F4D");
            if (te != null)
                logEntry = Hex.FromHex(te.ValueHex);
        }
        if (logEntry is null || logEntry.Length < 2)
            return;

        int sfi = logEntry[0];
        int count = logEntry[1];

        // Log Format (9F4F) describes the layout of each log record (a DOL).
        var logFormat = app.Tags.FirstOrDefault(t => t.Tag == "9F4F");
        List<(string Tag, int Length)>? dol = logFormat != null
            ? TlvParser.ParseDol(Hex.FromHex(logFormat.ValueHex))
            : null;

        for (int rec = 1; rec <= count; rec++)
        {
            var resp = await ReadRecordAsync(sfi, rec);
            if (resp is not { IsSuccess: true } || resp.Data.Length == 0)
                continue;
            var tx = ParseLogRecord(resp.Data, dol);
            if (tx != null)
                app.Transactions.Add(tx);
        }
    }

    private EmvTransaction? ParseLogRecord(byte[] data, List<(string Tag, int Length)>? dol)
    {
        if (dol == null)
            return new EmvTransaction { RawHex = data.ToHexSpaced() };

        var map = new Dictionary<string, byte[]>();
        int i = 0;
        foreach (var (tag, len) in dol)
        {
            if (i + len > data.Length)
                break;
            map[tag.ToUpperInvariant()] = data.Skip(i).Take(len).ToArray();
            i += len;
        }

        byte[]? Get(string t) => map.TryGetValue(t, out var v) ? v : null;

        return new EmvTransaction
        {
            Date = Get("9A") is { } d ? EmvDecoder.Decode("9A", d) : null,
            Time = Get("9F21") is { } tm ? EmvDecoder.Decode("9F21", tm) : null,
            Amount = Get("9F02") is { } a ? EmvDecoder.Decode("9F02", a) : null,
            Currency = Get("5F2A") is { } c ? EmvDecoder.CurrencyName(c) : null,
            Country = Get("9F1A") is { } co ? EmvDecoder.CountryName(co) : null,
            Type = Get("9C") is { } ty ? EmvDecoder.Decode("9C", ty) : null,
            Atc = Get("9F36") is { } atc ? (int)EmvDecoder.Int(atc) : null,
            Merchant = Get("9F4E") is { } m ? EmvDecoder.Ascii(m) : null,
            RawHex = data.ToHexSpaced(),
        };
    }

    // ---------------------------------------------------------------- tag recording

    private void RecordTags(IEnumerable<TlvNode> nodes, EmvApplication app, string source)
    {
        foreach (var node in nodes.SelectMany(n => n.Flatten()))
        {
            if (node.Constructed)
                continue; // templates carry no leaf value of their own
            AddTag(app, node.Tag, node.Value, source);
        }
    }

    private void AddTag(EmvApplication app, string tag, byte[] value, string source)
    {
        var entry = new EmvTagValue
        {
            Tag = tag,
            Name = EmvTags.Name(tag),
            ValueHex = value.ToHex(),
            Decoded = EmvDecoder.Decode(tag, value),
            Source = source,
        };
        app.Tags.Add(entry);
        _result.AllTags.Add(entry);
    }

    // ---------------------------------------------------------------- convenience fields

    private void PopulateConvenienceFields()
    {
        string? First(string tag) =>
            _result.AllTags.FirstOrDefault(t => t.Tag == tag)?.ValueHex;
        byte[]? FirstBytes(string tag) =>
            First(tag) is { } h ? Hex.FromHex(h) : null;

        if (FirstBytes("5A") is { } pan)
            _result.Pan = EmvDecoder.FormatPan(EmvDecoder.Bcd(pan));
        if (FirstBytes("5F34") is { } seq)
            _result.PanSequenceNumber = EmvDecoder.Int(seq).ToString();
        if (FirstBytes("5F24") is { } exp)
            _result.Expiry = EmvDecoder.Decode("5F24", exp);
        if (FirstBytes("5F25") is { } eff)
            _result.EffectiveDate = EmvDecoder.Decode("5F25", eff);
        if (FirstBytes("5F20") is { } name)
            _result.CardholderName = EmvDecoder.Ascii(name);
        if (FirstBytes("5F28") is { } ctry)
            _result.IssuerCountry = EmvDecoder.CountryName(ctry);
        if (FirstBytes("9F36") is { } atc)
            _result.Atc = (int)EmvDecoder.Int(atc);
        if (FirstBytes("9F13") is { } latc)
            _result.LastOnlineAtc = (int)EmvDecoder.Int(latc);
        if (FirstBytes("9F17") is { } pin)
            _result.PinTriesRemaining = (int)EmvDecoder.Int(pin);

        // Track 2 fills any gaps (PAN / expiry / service code) and feeds the service code.
        if (FirstBytes("57") is { } t2)
        {
            var parsed = EmvDecoder.DecodeTrack2(t2);
            _result.Track2 = parsed.Display;
            _result.Pan ??= parsed.Pan != null ? EmvDecoder.FormatPan(parsed.Pan) : null;
            _result.Expiry ??= parsed.Expiry;
            _result.ServiceCode = parsed.ServiceCode;
        }

        var firstApp = _result.Applications.FirstOrDefault();
        _result.Aid = firstApp?.Aid;
        _result.ApplicationLabel = firstApp?.Label
            ?? _result.AllTags.FirstOrDefault(t => t.Tag == "50")?.Decoded
            ?? _result.AllTags.FirstOrDefault(t => t.Tag == "9F12")?.Decoded;
        if (FirstBytes("5F2A") is { } cur)
            _result.Currency = EmvDecoder.CurrencyName(cur);
        _result.Scheme = GuessScheme(firstApp?.Aid);
    }

    private static string? GuessScheme(string? aid)
    {
        if (string.IsNullOrEmpty(aid))
            return null;
        aid = aid.ToUpperInvariant();
        if (aid.StartsWith("A000000003")) return "Visa";
        if (aid.StartsWith("A000000004") || aid.StartsWith("A000000005")) return "Mastercard";
        if (aid.StartsWith("A00000002501") || aid.StartsWith("A000000025")) return "American Express";
        if (aid.StartsWith("A000000065")) return "JCB";
        if (aid.StartsWith("A000000152") || aid.StartsWith("A000000324")) return "Discover";
        if (aid.StartsWith("A000000333")) return "UnionPay";
        if (aid.StartsWith("A0000000043060")) return "Maestro";
        return null;
    }

    // ---------------------------------------------------------------- APDU plumbing

    private Task<ApduResponse?> SelectByNameAsync(byte[] name, string label)
    {
        var apdu = BuildApdu(0x00, 0xA4, 0x04, 0x00, name, le: 0x00);
        return SendAsync(apdu, label);
    }

    /// <summary>Send an APDU, transparently handling 61xx (GET RESPONSE) and 6Cxx (wrong Le).</summary>
    private async Task<ApduResponse?> SendAsync(byte[] apdu, string label, bool logFailures = true)
    {
        try
        {
            var raw = await _t.TransceiveAsync(apdu);
            var resp = ApduResponse.FromRaw(raw);
            Log(label, apdu, raw);

            // 61xx: more data available -> GET RESPONSE
            if (resp.Sw1 == 0x61)
            {
                var getResp = BuildApdu(0x00, 0xC0, 0x00, 0x00, null, le: resp.Sw2);
                raw = await _t.TransceiveAsync(getResp);
                resp = ApduResponse.FromRaw(raw);
                Log($"{label} (GET RESPONSE)", getResp, raw);
            }
            // 6Cxx: wrong Le -> resend with the exact length the card wants
            else if (resp.Sw1 == 0x6C && apdu.Length >= 1)
            {
                var fixedApdu = (byte[])apdu.Clone();
                fixedApdu[^1] = resp.Sw2;
                raw = await _t.TransceiveAsync(fixedApdu);
                resp = ApduResponse.FromRaw(raw);
                Log($"{label} (retry Le={resp.Sw2:X2})", fixedApdu, raw);
            }

            if (!resp.IsSuccess && logFailures)
                _result.Warnings.Add($"{label}: SW={resp.StatusHex}");
            return resp;
        }
        catch (Exception ex)
        {
            if (logFailures)
                _result.Warnings.Add($"{label}: {ex.Message}");
            _result.ApduLog.Add($"{label}\n  >> {apdu.ToHexSpaced()}\n  !! {ex.Message}");
            return null;
        }
    }

    private void Log(string label, byte[] cmd, byte[] resp)
        => _result.ApduLog.Add($"{label}\n  >> {cmd.ToHexSpaced()}\n  << {resp.ToHexSpaced()}");

    /// <summary>Build a case-2/3/4 APDU. Pass data=null for no command data; le=null to omit Le.</summary>
    private static byte[] BuildApdu(byte cla, byte ins, byte p1, byte p2, byte[]? data, byte? le)
    {
        var ms = new MemoryStream();
        ms.WriteByte(cla);
        ms.WriteByte(ins);
        ms.WriteByte(p1);
        ms.WriteByte(p2);
        if (data is { Length: > 0 })
        {
            ms.WriteByte((byte)data.Length);
            ms.Write(data, 0, data.Length);
        }
        if (le.HasValue)
            ms.WriteByte(le.Value);
        return ms.ToArray();
    }

    private static byte[] DateYymmdd()
    {
        var now = DateTime.UtcNow;
        return new[]
        {
            (byte)(((now.Year % 100 / 10) << 4) | (now.Year % 10)),
            (byte)(((now.Month / 10) << 4) | (now.Month % 10)),
            (byte)(((now.Day / 10) << 4) | (now.Day % 10)),
        };
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    private static byte[]? FindValue(IEnumerable<TlvNode> nodes, string tag)
        => nodes.SelectMany(n => n.Flatten()).FirstOrDefault(n => n.Tag == tag)?.Value;

    private readonly record struct AidCandidate(string Aid, string? Label, int? Priority);

    private static readonly string[] KnownAids =
    {
        "A0000000031010", "A0000000032010", "A0000000033010", "A0000000038010", // Visa
        "A0000000041010", "A0000000042203", "A0000000043060",                   // Mastercard / Maestro
        "A00000002501",   "A000000025010402", "A000000025010701",               // Amex
        "A0000000651010",                                                       // JCB
        "A0000001523010", "A0000003241010",                                     // Discover
        "A000000333010101", "A000000333010102", "A000000333010103",             // UnionPay
    };
}
