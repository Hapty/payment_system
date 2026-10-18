using PaymentSystem.Iso8583;

namespace PaymentSystem.Payment.Transactions;

// İşlem onaylanmadığında TransactionHandler'ın fırlattığı exception: iş reddi (51, 14, 57 ...) ya da teknik hata (91, 96).
// Red kaydı DebitTransaction'a F39'u ile commit edildikten sonra fırlatılır, kayıt geri alınmaz.
// Kart numarası daima maskelidir (PCI DSS): exception'ın mesajı ve alanları loglara düşer.
public sealed class TransactionException : Exception
{
    public TransactionException(
        string responseCode, string? irc, string description, string cardNumber, Guid? transactionId, long? accountNo,
        Exception? innerException = null)
        : base(Format(responseCode, irc, description, PanMask.Mask(cardNumber), transactionId, accountNo), innerException)
    {
        ResponseCode = responseCode;
        Irc = irc;
        Description = description;
        CardNumber = PanMask.Mask(cardNumber);
        TransactionId = transactionId;
        AccountNo = accountNo;
    }

    // POS'a dönen F39.
    public string ResponseCode { get; }

    // İç karar kodu; teknik hatada ve tekrar gelen istekte (kayıtta IRC tutulmaz) null.
    public string? Irc { get; }

    // InternalResponseCode tablosundaki açıklama ya da teknik hatanın açıklaması.
    public string Description { get; }

    // Maskeli: ilk 6 ve son 2 hane.
    public string CardNumber { get; }

    // DebitTransaction.Guid; işlem tipi tanımsızsa (kayıt açılmaz) null.
    public Guid? TransactionId { get; }

    // Kart bulunamadıysa ya da karar karttan önce verildiyse null.
    public long? AccountNo { get; }

    // 91 (örn. DB'ye ulaşılamadı) ve 96 (IRC tanımsız): iş reddi değil, sistem hatası.
    public bool IsTechnical => ResponseCode is ResponseCodes.SystemUnavailable or ResponseCodes.SystemMalfunction;

    private static string Format(string responseCode, string? irc, string description, string maskedPan, Guid? transactionId, long? accountNo) =>
        $"F39 {responseCode} {description} (IRC {irc ?? "-"}) transaction {transactionId?.ToString() ?? "-"} PAN {maskedPan} account {accountNo?.ToString() ?? "-"}";
}
