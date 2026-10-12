namespace PaymentSystem.Iso8583;

public enum IsoLengthType
{
    Fixed,      // her zaman tam olarak Length karakter
    LLVar,      // önce 2 haneli uzunluk, sonra en fazla Length karakter
    LLLVar      // önce 3 haneli uzunluk, sonra en fazla Length karakter
}

public enum IsoCharset
{
    Numeric,        // n   : sadece rakam
    AlphaNumeric,   // an  : harf ve rakam
    Any             // ans : yazdırılabilir her ASCII karakter (harf, rakam, boşluk, sembol)
}

// Bir ISO8583 alanının mesaj içinde nasıl yer aldığını tarif eder.
public sealed record IsoFieldDefinition(int Number, string Name, IsoLengthType LengthType, int Length, IsoCharset Charset)
{
    public int PrefixDigits => LengthType switch
    {
        IsoLengthType.LLVar => 2,
        IsoLengthType.LLLVar => 3,
        _ => 0
    };

    // Değer bu alana uymuyorsa nedenini, geçerliyse null döner.
    public string? Validate(string value)
    {
        if (LengthType == IsoLengthType.Fixed && value.Length != Length)
            return $"Length must be exactly {Length}, but was {value.Length}.";

        if (LengthType != IsoLengthType.Fixed && (value.Length == 0 || value.Length > Length))
            return $"Length must be between 1 and {Length}, but was {value.Length}.";

        var allowed = Charset switch
        {
            IsoCharset.Numeric => value.All(char.IsAsciiDigit),
            IsoCharset.AlphaNumeric => value.All(char.IsAsciiLetterOrDigit),
            _ => value.All(c => c is >= ' ' and <= '~')
        };
        return allowed ? null : $"Contains characters that are not allowed for {Charset}.";
    }
}
