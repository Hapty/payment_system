using System.Globalization;
using System.Text;
using PaymentSystem.Notification.Events;

namespace PaymentSystem.Notification.Email;

// Satış ve transfer e-postalarının ortak biçimi: tutar, TL karşılığı, tarih ve işlem no satırları.
internal static class EmailFormat
{
    public const string TurkishLira = "949";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // ISO 4217 sayısal -> harf kodu; listede olmayan kod olduğu gibi yazılır.
    private static readonly Dictionary<string, string> Currencies = new()
    {
        ["949"] = "TRY", ["840"] = "USD", ["978"] = "EUR", ["826"] = "GBP", ["756"] = "CHF", ["392"] = "JPY",
        ["036"] = "AUD", ["124"] = "CAD", ["208"] = "DKK", ["578"] = "NOK", ["752"] = "SEK", ["682"] = "SAR"
    };

    public const string Footer = "Bu e-posta otomatik olarak gönderilmiştir, lütfen yanıtlamayınız.";

    public static string Amount(TransactionCompleted transaction) => Money(transaction.Amount, transaction.CurrencyCode);

    public static StringBuilder AppendAmount(this StringBuilder body, TransactionCompleted transaction)
    {
        body.AppendLine($"Tutar        : {Amount(transaction)}");
        if (transaction.CurrencyCode != TurkishLira && transaction.LocalAmount is { } lira)
            body.AppendLine($"TL karşılığı : {Money(lira, TurkishLira)}");
        return body;
    }

    public static StringBuilder AppendDateAndId(this StringBuilder body, TransactionCompleted transaction) =>
        body.AppendLine($"Tarih / saat : {DateTime(transaction.TransactionDate, transaction.LocalTime)}")
            .AppendLine($"İşlem no     : {transaction.TransactionId}");

    private static string Money(decimal value, string currencyCode) =>
        $"{value.ToString("N2", Turkish)} {Currencies.GetValueOrDefault(currencyCode, currencyCode)}";

    // yyyymmdd + hhmmss -> 15.03.2026 12:59:59; beklenmeyen formatta olduğu gibi yazılır.
    private static string DateTime(string date, string time) =>
        date.Length == 8 && time.Length == 6
            ? $"{date[6..8]}.{date[4..6]}.{date[..4]} {time[..2]}:{time[2..4]}:{time[4..6]}"
            : $"{date} {time}".Trim();
}
