namespace PaymentSystem.Payment.Transactions;

// Payment'ın tablodan değil koddan ürettiği ISO8583 alan 39 (cevap kodu) değerleri: teknik hatalar.
// DB'ye ulaşılamadığında InternalResponseCode tablosu da okunamaz; bu kodlar o yüzden sabittir.
// İş kararlarının F39'u InternalResponseCodes (IRC) üzerinden tablodan gelir.
public static class ResponseCodes
{
    public const string FormatError = "30";                 // istek mesajı okunamadı / alanlar geçersiz
    public const string SystemUnavailable = "91";           // işlem şu an yapılamıyor (örn. DB'ye ulaşılamadı), sonra tekrar denenebilir
    public const string SystemMalfunction = "96";           // karar verildi ama IRC'nin F39 karşılığı tabloda tanımlı değil
}
