namespace PaymentSystem.Iso8583;

// Card numbers must never be written to logs in full (PCI DSS): keep the first 6 and last 4 digits.
public static class PanMask
{
    public static string Mask(string? pan) => pan switch
    {
        null => "",
        { Length: < 10 } => new string('*', pan.Length),
        _ => $"{pan[..6]}{new string('*', pan.Length - 10)}{pan[^4..]}"
    };
}
