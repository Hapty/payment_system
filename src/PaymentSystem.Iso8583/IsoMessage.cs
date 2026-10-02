namespace PaymentSystem.Iso8583;

// An ISO8583 message in memory: the MTI plus the data elements that are present, keyed by field number.
public sealed class IsoMessage(string mti)
{
    private readonly SortedDictionary<int, string> _fields = new();

    public string Mti { get; } = mti;

    public IReadOnlyDictionary<int, string> Fields => _fields;

    public string? this[int field] => _fields.GetValueOrDefault(field);

    public bool Has(int field) => _fields.ContainsKey(field);

    // Returns the message itself so fields can be chained: new IsoMessage("0800").Set(11, "000001").Set(70, "301")
    public IsoMessage Set(int field, string value)
    {
        // Field 1 is the secondary bitmap; the packer manages it.
        ArgumentOutOfRangeException.ThrowIfLessThan(field, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(field, 128);
        _fields[field] = value;
        return this;
    }

    public static bool IsValidMti(string mti) => mti.Length == 4 && mti.All(char.IsAsciiDigit);

    // The third MTI digit is the message function: even = request/advice/notification, odd = the matching response.
    public bool IsRequest => IsValidMti(Mti) && (Mti[2] - '0') % 2 == 0;

    // 0200 -> 0210, 0800 -> 0810, 0400 -> 0410
    public string ToResponseMti()
    {
        if (!IsRequest)
            throw new InvalidOperationException($"MTI {Mti} is not a request, so it has no response MTI.");
        return $"{Mti[..2]}{(char)(Mti[2] + 1)}{Mti[3]}";
    }
}
