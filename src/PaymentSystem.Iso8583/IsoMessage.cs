namespace PaymentSystem.Iso8583;

// Bellekteki bir ISO8583 mesajı: MTI ve mesajda bulunan alanlar (alan numarasına göre).
public sealed class IsoMessage(string mti)
{
    private readonly SortedDictionary<int, string> _fields = new();

    public string Mti { get; } = mti;

    public IReadOnlyDictionary<int, string> Fields => _fields;

    public string? this[int field] => _fields.GetValueOrDefault(field);

    public bool Has(int field) => _fields.ContainsKey(field);

    // Mesajın kendisini döner, böylece alanlar zincirleme eklenebilir: new IsoMessage("0800").Set(11, "000001").Set(70, "301")
    public IsoMessage Set(int field, string value)
    {
        // Alan 1 ikincil bitmap'tir; onu packer yönetir.
        ArgumentOutOfRangeException.ThrowIfLessThan(field, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(field, 128);
        _fields[field] = value;
        return this;
    }

    public static bool IsValidMti(string mti) => mti.Length == 4 && mti.All(char.IsAsciiDigit);

    // MTI'nın 3. hanesi mesajın işlevidir: çift = istek/bildirim, tek = o isteğin cevabı.
    public bool IsRequest => IsValidMti(Mti) && (Mti[2] - '0') % 2 == 0;

    // 0200 -> 0210, 0800 -> 0810, 0400 -> 0410
    public string ToResponseMti()
    {
        if (!IsRequest)
            throw new InvalidOperationException($"MTI {Mti} is not a request, so it has no response MTI.");
        return $"{Mti[..2]}{(char)(Mti[2] + 1)}{Mti[3]}";
    }
}
