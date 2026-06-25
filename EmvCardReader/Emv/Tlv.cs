namespace EmvCardReader.Emv;

/// <summary>A single BER-TLV node. Constructed nodes carry nested <see cref="Children"/>.</summary>
public sealed class TlvNode
{
    public required string Tag { get; init; }      // hex, e.g. "5F24"
    public required byte[] Value { get; init; }     // raw value bytes
    public bool Constructed { get; init; }
    public List<TlvNode> Children { get; } = new();

    public string ValueHex => Value.ToHex();

    /// <summary>Depth-first enumeration of this node and all descendants.</summary>
    public IEnumerable<TlvNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var n in child.Flatten())
                yield return n;
    }
}

/// <summary>Minimal, defensive BER-TLV parser for EMV data objects.</summary>
public static class TlvParser
{
    /// <summary>Parse a buffer into top-level TLV nodes (recursing into constructed ones).</summary>
    public static List<TlvNode> Parse(byte[] data)
        => Parse(data, 0, data.Length);

    private static List<TlvNode> Parse(byte[] data, int offset, int end)
    {
        var nodes = new List<TlvNode>();
        int i = offset;
        while (i < end)
        {
            // Skip padding bytes (00 / FF) that appear between TLVs.
            if (data[i] == 0x00 || data[i] == 0xFF)
            {
                i++;
                continue;
            }

            // ---- Tag ----
            int tagStart = i;
            byte first = data[i];
            bool constructed = (first & 0x20) != 0;
            i++;
            if ((first & 0x1F) == 0x1F)
            {
                // Multi-byte tag: continue while high bit set.
                while (i < end && (data[i] & 0x80) != 0)
                    i++;
                if (i < end)
                    i++; // last tag byte (high bit clear)
            }
            if (i > end)
                break;
            string tag = Convert.ToHexString(data, tagStart, i - tagStart);

            // ---- Length ----
            if (i >= end)
                break;
            int length;
            byte lenByte = data[i++];
            if ((lenByte & 0x80) == 0)
            {
                length = lenByte;
            }
            else
            {
                int numBytes = lenByte & 0x7F;
                if (numBytes == 0 || i + numBytes > end)
                    break; // indefinite / malformed length – bail out gracefully
                length = 0;
                for (int k = 0; k < numBytes; k++)
                    length = (length << 8) | data[i++];
            }

            if (length < 0 || i + length > end)
            {
                // Truncated value: clamp so we still surface what we have.
                length = Math.Max(0, end - i);
            }

            var value = new byte[length];
            Array.Copy(data, i, value, 0, length);
            i += length;

            var node = new TlvNode { Tag = tag, Value = value, Constructed = constructed };
            if (constructed && length > 0)
                node.Children.AddRange(Parse(value, 0, value.Length));

            nodes.Add(node);
        }
        return nodes;
    }

    /// <summary>Parse a Data Object List (DOL: a flat sequence of tag+length pairs, no values).</summary>
    public static List<(string Tag, int Length)> ParseDol(byte[] data)
    {
        var list = new List<(string, int)>();
        int i = 0;
        while (i < data.Length)
        {
            int tagStart = i;
            byte first = data[i++];
            if ((first & 0x1F) == 0x1F)
            {
                while (i < data.Length && (data[i] & 0x80) != 0)
                    i++;
                if (i < data.Length)
                    i++;
            }
            string tag = Convert.ToHexString(data, tagStart, i - tagStart);
            if (i >= data.Length)
                break;
            int length = data[i++];
            list.Add((tag, length));
        }
        return list;
    }
}
