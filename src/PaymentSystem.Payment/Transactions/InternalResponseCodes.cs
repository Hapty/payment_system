namespace PaymentSystem.Payment.Transactions;

// TransactionHandler'ın kararı: her sebebin kendi iç kodu (IRC). POS'a dönen F39, dbo.InternalResponseCode tablosundan okunur;
// birkaç IRC aynı F39'a çıkabilir (örn. 1001..1004 -> 12). Tabloda olmayan bir IRC 96 ile cevaplanır.
public static class InternalResponseCodes
{
    public const string Approved = "0000";
    public const string TransactionTypeNotDefined = "1001";     // MTI + F3 tanımsız
    public const string EntryModeNotDefined = "1002";           // F22 EntryMode tablosunda yok
    public const string TransactionNotSupported = "1003";       // tip tanımlı ama işlenmiyor (MoneyTransfer)
    public const string CurrencyNotSupported = "1004";          // TCMB bu para birimi için kur yayımlamıyor
    public const string CardNotFound = "2001";
    public const string CardExpired = "2002";                   // süresi dolmuş ya da F14 kartla uyuşmuyor
    public const string AccountNotActive = "2003";
    public const string ContactlessNotAllowed = "2004";
    public const string ContactlessLimitExceeded = "2005";
    public const string ZeroAmount = "3001";
    public const string InsufficientFunds = "3002";
    public const string ExchangeRateUnavailable = "9001";       // cache'te kur yok ya da bayat
}
