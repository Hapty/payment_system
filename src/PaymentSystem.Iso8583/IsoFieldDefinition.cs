namespace PaymentSystem.Iso8583;

public enum IsoLengthType
{
    Fixed,      // always exactly Length characters
    LLVar,      // 2-digit length prefix, then up to Length characters
    LLLVar      // 3-digit length prefix, then up to Length characters
}

public enum IsoCharset
{
    Numeric,        // n   : digits only
    AlphaNumeric,   // an  : letters and digits
    Any             // ans : any printable ASCII (letters, digits, space, symbols)
}

// Describes how one ISO8583 data element is laid out on the wire.
public sealed record IsoFieldDefinition(int Number, string Name, IsoLengthType LengthType, int Length, IsoCharset Charset)
{
    public int PrefixDigits => LengthType switch
    {
        IsoLengthType.LLVar => 2,
        IsoLengthType.LLLVar => 3,
        _ => 0
    };

    // Returns why the value does not fit this field, or null when it is valid.
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
