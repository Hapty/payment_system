namespace PaymentSystem.Payment.Transactions;

// Payment'ın ürettiği ISO8583 alan 39 (cevap kodu) değerleri. Gate bunları olduğu gibi POS'a iletir.
public static class ResponseCodes
{
    public const string Approved = "00";                    // onaylandı
    public const string InvalidTransaction = "12";          // geçersiz / desteklenmeyen işlem
    public const string InvalidAmount = "13";               // geçersiz tutar
    public const string InvalidCardNumber = "14";           // kart bulunamadı
    public const string InsufficientFunds = "51";           // yetersiz bakiye
    public const string ExpiredCard = "54";                 // kartın süresi dolmuş / son kullanma tarihi uyuşmuyor
    public const string NotPermittedToCardholder = "57";    // kart bu işleme kapalı (örn. temassız)
    public const string FormatError = "30";                 // istek mesajı okunamadı / alanlar geçersiz
    public const string RestrictedCard = "62";              // hesap aktif değil
    public const string ContactlessLimitExceeded = "65";    // temassız limit aşıldı: terminal çip + PIN ile tekrar denemeli
    public const string SystemUnavailable = "91";           // işlem şu an yapılamıyor (örn. döviz kuru yok), sonra tekrar denenebilir
}
