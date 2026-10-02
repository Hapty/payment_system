namespace PaymentSystem.Gate;

// ISO8583 field 39 values that the Gate itself produces. Any other code comes from the Payment service as-is.
public static class ResponseCodes
{
    public const string Approved = "00";
    public const string InvalidTransaction = "12";
    public const string FormatError = "30";
    public const string PaymentUnavailable = "91";   // "issuer or switch inoperative"
}
