namespace PaymentSystem.Iso8583;

// Kart numaraları loglara asla tam olarak yazılmamalı (PCI DSS): sadece ilk 6 ve son 4 hane bırakılır.
public static class PanMask
{
    public static string Mask(string? pan) => pan switch
    {
        null => "",
        { Length: < 10 } => new string('*', pan.Length),
        _ => $"{pan[..6]}{new string('*', pan.Length - 10)}{pan[^4..]}"
    };
}
