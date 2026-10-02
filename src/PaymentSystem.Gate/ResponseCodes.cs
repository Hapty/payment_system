namespace PaymentSystem.Gate;

// Gate'in kendisinin ürettiği ISO8583 alan 39 (cevap kodu) değerleri. Diğer kodlar Payment servisinden olduğu gibi gelir.
public static class ResponseCodes
{
    public const string Approved = "00";              // onaylandı
    public const string InvalidTransaction = "12";    // geçersiz işlem
    public const string FormatError = "30";           // format hatası
    public const string PaymentUnavailable = "91";    // karşı sistem (issuer/switch) çalışmıyor
}
