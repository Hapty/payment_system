using System.Text;

namespace PaymentSystem.Iso8583;

// Converts between IsoMessage and its wire bytes:
//   [MTI: 4 ASCII digits][primary bitmap: 8 bytes][secondary bitmap: 8 bytes, only if bit 1 is set][fields, ASCII, in field order]
// Bit n of the bitmap (1-based, most significant bit of the first byte = bit 1) says whether field n is present.
public sealed class IsoMessagePacker(IReadOnlyDictionary<int, IsoFieldDefinition> definitions)
{
    private const int BitmapSize = 8;

    public static IsoMessagePacker Default { get; } = new(Iso87Fields.Default);

    public byte[] Pack(IsoMessage message)
    {
        if (!IsoMessage.IsValidMti(message.Mti))
            throw new IsoFormatException(0, $"MTI must be 4 digits, but was '{message.Mti}'.");

        var hasSecondary = message.Fields.Keys.Any(f => f > 64);
        var bitmap = new byte[hasSecondary ? 2 * BitmapSize : BitmapSize];
        if (hasSecondary) SetBit(bitmap, 1);

        var body = new StringBuilder();
        foreach (var (number, value) in message.Fields)
        {
            var definition = Definition(number, message.Mti);
            if (definition.Validate(value) is { } error)
                throw new IsoFormatException(number, error, message.Mti);

            SetBit(bitmap, number);
            if (definition.PrefixDigits > 0)
                body.Append(value.Length.ToString($"D{definition.PrefixDigits}"));
            body.Append(value);
        }

        return [.. Encoding.ASCII.GetBytes(message.Mti), .. bitmap, .. Encoding.ASCII.GetBytes(body.ToString())];
    }

    public IsoMessage Unpack(byte[] data)
    {
        if (data.Length < 4)
            throw new IsoFormatException(0, $"Message is {data.Length} bytes, too short to hold an MTI.");

        // Latin1 maps every byte to one char, so non-ASCII bytes survive decoding and are rejected by validation.
        var mti = Encoding.Latin1.GetString(data, 0, 4);
        if (!IsoMessage.IsValidMti(mti))
            throw new IsoFormatException(0, $"MTI must be 4 digits, but was '{mti}'.");

        var position = 4;
        string Take(int count, int field, string what)
        {
            if (position + count > data.Length)
                throw new IsoFormatException(field, $"{what} is truncated.", mti);
            var text = Encoding.Latin1.GetString(data, position, count);
            position += count;
            return text;
        }

        var bitmap = TakeBytes(data, ref position, BitmapSize) ?? throw new IsoFormatException(1, "Primary bitmap is truncated.", mti);
        if (IsBitSet(bitmap, 1))
            bitmap = [.. bitmap, .. TakeBytes(data, ref position, BitmapSize) ?? throw new IsoFormatException(1, "Secondary bitmap is truncated.", mti)];

        var message = new IsoMessage(mti);
        for (var number = 2; number <= bitmap.Length * 8; number++)
        {
            if (!IsBitSet(bitmap, number)) continue;

            var definition = Definition(number, mti);
            var length = definition.Length;
            if (definition.PrefixDigits > 0)
            {
                var prefix = Take(definition.PrefixDigits, number, "Length prefix");
                if (!prefix.All(char.IsAsciiDigit))
                    throw new IsoFormatException(number, $"Length prefix '{prefix}' is not numeric.", mti);
                length = int.Parse(prefix);
            }

            var value = Take(length, number, "Field value");
            if (definition.Validate(value) is { } error)
                throw new IsoFormatException(number, error, mti);
            message.Set(number, value);
        }

        if (position != data.Length)
            throw new IsoFormatException(0, $"{data.Length - position} unexpected bytes after the last field.", mti);

        return message;
    }

    private IsoFieldDefinition Definition(int number, string mti) =>
        definitions.TryGetValue(number, out var definition)
            ? definition
            : throw new IsoFormatException(number, "Field is not supported by this system.", mti);

    private static byte[]? TakeBytes(byte[] data, ref int position, int count)
    {
        if (position + count > data.Length) return null;
        var bytes = data[position..(position + count)];
        position += count;
        return bytes;
    }

    private static void SetBit(byte[] bitmap, int field) =>
        bitmap[(field - 1) / 8] |= (byte)(0x80 >> ((field - 1) % 8));

    private static bool IsBitSet(byte[] bitmap, int field) =>
        (bitmap[(field - 1) / 8] & (0x80 >> ((field - 1) % 8))) != 0;
}
