namespace PaymentSystem.Gate;

// Bound from the "Gate" section of appsettings.json, or env vars such as Gate__PaymentBaseUrl.
public class GateOptions
{
    public const string SectionName = "Gate";

    // TCP port POS terminals connect to. 0 lets the OS pick a free port (used by tests).
    public int Port { get; set; } = 8583;

    public string PaymentBaseUrl { get; set; } = "http://localhost:5002";

    public int PaymentTimeoutSeconds { get; set; } = 10;
}
