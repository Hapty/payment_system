namespace PaymentSystem.Iso8583;

// Thrown when bytes cannot be read as an ISO8583 message, or a message cannot be written as one.
// FieldNumber is 0 for MTI/structure problems and 1 for bitmap problems.
// Mti is set when the MTI itself was readable, so the receiver can still send a format-error response.
public sealed class IsoFormatException(int fieldNumber, string reason, string? mti = null)
    : Exception($"ISO8583 format error in field {fieldNumber}: {reason}")
{
    public int FieldNumber { get; } = fieldNumber;
    public string Reason { get; } = reason;
    public string? Mti { get; } = mti;
}
