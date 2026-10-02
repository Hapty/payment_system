namespace PaymentSystem.Gate;

// appsettings.json'daki "Gate" bölümünden ya da Gate__PaymentBaseUrl gibi ortam değişkenlerinden doldurulur.
public class GateOptions
{
    public const string SectionName = "Gate";

    // POS terminallerinin bağlandığı TCP portu. 0 verilirse işletim sistemi boş bir port seçer (testlerde kullanılır).
    public int Port { get; set; } = 8583;

    public string PaymentBaseUrl { get; set; } = "http://localhost:5002";

    public int PaymentTimeoutSeconds { get; set; } = 10;
}
