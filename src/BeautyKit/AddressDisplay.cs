using System.Globalization;
using System.Text;

namespace BeautyKit;

/// <summary>How an address is shown in the address bar: readable, but never altered in a way that changes where it goes.</summary>
public static class AddressDisplay
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// "https://es.wikipedia.org/wiki/Espa%C3%B1a" → ".../Espa&#241;a". Only escaped letters and symbols from other
    /// alphabets are decoded; every escape of an ASCII character stays (%20, %2F, %25, %3F, %23, %26…), because
    /// decoding those changes the address (a space would make the text a search). Escapes that are not valid UTF-8, and
    /// characters that hide or reorder text (control, bidirectional marks, spaces), stay escaped too. The host is not
    /// touched. Typing the result back gives the same address.
    /// </summary>
    public static string ToDisplay(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.IndexOf('%') < 0) return url ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return url;

        // Everything before the path (scheme, user info, host, port) is kept as it is.
        var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathStart = url.IndexOfAny(['/', '?', '#'], start);
        if (pathStart < 0) return url;

        var result = new StringBuilder(url.Length);
        result.Append(url, 0, pathStart);

        for (var i = pathStart; i < url.Length;)
        {
            if (!TryHighByte(url, i, out _))
            {
                result.Append(url[i++]);
                continue;
            }

            // A run of consecutive escapes of non-ASCII bytes: decode it as UTF-8, a character at a time.
            var bytes = new List<byte>();
            var end = i;
            while (TryHighByte(url, end, out var b))
            {
                bytes.Add(b);
                end += 3;
            }

            AppendDecoded(result, url, i, bytes);
            i = end;
        }

        return result.ToString();
    }

    private static void AppendDecoded(StringBuilder result, string url, int escapesStart, List<byte> bytes)
    {
        var offset = 0; // position in bytes
        while (offset < bytes.Count)
        {
            var length = Utf8SequenceLength(bytes[offset]);
            string? text = null;
            if (length > 0 && offset + length <= bytes.Count)
            {
                try
                {
                    text = StrictUtf8.GetString(bytes.GetRange(offset, length).ToArray());
                }
                catch (DecoderFallbackException)
                {
                    // not a valid character: stays escaped
                }
            }

            if (text is not null && IsReadable(text))
            {
                result.Append(text);
                offset += length;
            }
            else
            {
                // One byte stays escaped; the next one is looked at again.
                result.Append(url, escapesStart + offset * 3, 3);
                offset++;
            }
        }
    }

    private static int Utf8SequenceLength(byte first) => first switch
    {
        >= 0xC2 and <= 0xDF => 2,
        >= 0xE0 and <= 0xEF => 3,
        >= 0xF0 and <= 0xF4 => 4,
        _ => 0,
    };

    private static bool IsReadable(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:            // bidirectional marks, zero-width characters
                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.OtherNotAssigned:
                    return false;
            }
        }
        return true;
    }

    /// <summary>%XX at <paramref name="index"/> with a value of 0x80 or more.</summary>
    private static bool TryHighByte(string text, int index, out byte value)
    {
        value = 0;
        return index + 2 < text.Length && text[index] == '%' &&
               byte.TryParse(text.AsSpan(index + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) &&
               value >= 0x80;
    }
}
