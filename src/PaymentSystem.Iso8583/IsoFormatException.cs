namespace PaymentSystem.Iso8583;

// Baytlar ISO8583 mesajı olarak okunamadığında ya da bir mesaj ISO8583'e yazılamadığında fırlatılır.
// FieldNumber: MTI/yapı sorunlarında 0, bitmap sorunlarında 1, aksi halde sorunlu alanın numarası.
// Mti: MTI okunabildiyse doludur; böylece alıcı yine de "format hatası" cevabı gönderebilir.
public sealed class IsoFormatException(int fieldNumber, string reason, string? mti = null)
    : Exception($"ISO8583 format error in field {fieldNumber}: {reason}")
{
    public int FieldNumber { get; } = fieldNumber;
    public string Reason { get; } = reason;
    public string? Mti { get; } = mti;
}
