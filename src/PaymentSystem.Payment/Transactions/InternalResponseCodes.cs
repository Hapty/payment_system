namespace PaymentSystem.Payment.Transactions;

// TransactionHandler'ın kararı: her sebebin kendi iç kodu (IRC). POS'a dönen F39, dbo.InternalResponseCode tablosundan okunur;
// birkaç IRC aynı F39'a çıkabilir (örn. 1001..1004 -> 12). Tabloda olmayan bir IRC 96 ile cevaplanır.
public static class InternalResponseCodes
{
    public const string Approved = "0000";
    public const string TransactionTypeNotDefined = "1001";     // MTI + F3 tanımsız
    public const string EntryModeNotDefined = "1002";           // F22 EntryMode tablosunda yok
    public const string TransactionNotSupported = "1003";       // tip tanımlı ama işlenmiyor
    public const string CurrencyNotSupported = "1004";          // TCMB bu para birimi için kur yayımlamıyor
    public const string RecipientCardInvalid = "1005";          // transferde F48 (alıcı kart no) yok ya da kart numarası değil
    public const string SameAccountTransfer = "1006";           // transferde alıcı kart gönderenle aynı hesaba bağlı
    public const string MerchantIdMissing = "1007";            // F42 yok (Gate zorunlu tutar; kuyruğa başka yoldan gelirse)
    public const string CardNotFound = "2001";
    public const string CardExpired = "2002";                   // süresi dolmuş ya da F14 kartla uyuşmuyor
    public const string AccountNotActive = "2003";
    public const string ContactlessNotAllowed = "2004";
    public const string ContactlessLimitExceeded = "2005";
    public const string CvvMismatch = "2006";                   // CVV2 yanlış ya da kart fiziksel olarak yokken gelmedi
    public const string RecipientCardNotFound = "2007";
    public const string RecipientAccountNotActive = "2008";     // alıcı kartın hesabı yok ya da Active değil
    public const string ZeroAmount = "3001";
    public const string InsufficientFunds = "3002";
    public const string MerchantNotFound = "4001";             // F42 Merchant tablosunda yok
    public const string MerchantNotActive = "4002";
    public const string MerchantTypeMismatch = "4003";         // F18 geldi ama işyerinin MCC'si değil
    public const string ExchangeRateUnavailable = "9001";       // cache'te kur yok ya da bayat
}
