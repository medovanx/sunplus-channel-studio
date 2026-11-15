using System.Text;

namespace SunplusChannelStudio.Core;

/// <summary>
/// DVB SI text (ETSI EN 300 468 annex A): an optional leading byte below 0x20 picks the character table.
/// The receiver writes Arabic names as 0x02 + ISO-8859-6; plain names have no prefix.
/// </summary>
public static class DvbText
{
    static DvbText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    static readonly Encoding Latin = Encoding.Latin1;

    static Encoding? TableFor(byte selector) => selector switch
    {
        0x01 => Encoding.GetEncoding(28595), // ISO-8859-5 Cyrillic
        0x02 => Encoding.GetEncoding(28596), // ISO-8859-6 Arabic
        0x03 => Encoding.GetEncoding(28597), // ISO-8859-7 Greek
        0x04 => Encoding.GetEncoding(28598), // ISO-8859-8 Hebrew
        0x05 => Encoding.GetEncoding(28599), // ISO-8859-9 Turkish
        _ => null,
    };

    /// <summary>Decodes a zero-terminated fixed field.</summary>
    public static string Decode(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end >= 0) field = field[..end];
        if (field.IsEmpty) return string.Empty;
        if (field[0] < 0x20)
        {
            var table = TableFor(field[0]);
            return (table ?? Latin).GetString(field[1..]);
        }
        return Latin.GetString(field);
    }

    /// <summary>The table selector a field starts with, or null for the default table.</summary>
    public static byte? SelectorOf(ReadOnlySpan<byte> field) =>
        field.Length > 0 && field[0] is > 0 and < 0x20 ? field[0] : null;

    /// <summary>
    /// Encodes text for a fixed field of <paramref name="size"/> bytes, zero padded. The field's current
    /// selector is kept when the text still fits its table; Arabic text otherwise gets 0x02.
    /// Returns null when the text cannot fit or cannot be encoded.
    /// </summary>
    public static byte[]? Encode(string text, int size, byte? currentSelector = null)
    {
        byte[]? body = null;
        if (currentSelector is { } keep) body = TryTable(keep, text);
        if (body is null && NeedsArabic(text)) body = TryTable(0x02, text);
        if (body is null && text.All(c => c <= 0xFF)) body = Latin.GetBytes(text);
        // The receiver needs at least one terminating zero.
        if (body is null || body.Length >= size) return null;
        var field = new byte[size];
        body.CopyTo(field, 0);
        return field;
    }

    static byte[]? TryTable(byte selector, string text)
    {
        var table = TableFor(selector);
        if (table is null) return text.All(c => c <= 0xFF) ? [selector, .. Latin.GetBytes(text)] : null;
        var strict = Encoding.GetEncoding(table.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        try { return [selector, .. strict.GetBytes(text)]; }
        catch (EncoderFallbackException) { return null; }
    }

    /// <summary>Longest text that fits a field, for input limits.</summary>
    public static int MaxChars(string text, int size) => NeedsArabic(text) ? size - 2 : size - 1;

    static bool NeedsArabic(string text) => text.Any(c => c is >= '؀' and <= 'ۿ');
}
