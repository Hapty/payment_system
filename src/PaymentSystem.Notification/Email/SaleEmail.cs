using System.Globalization;
using System.Text;
using PaymentSystem.Notification.Events;

namespace PaymentSystem.Notification.Email;

// Onaylı satışın müşteriye giden e-postası. Kart numarası olaydan zaten maskeli gelir.
public static class SaleEmail
{
    private const string TurkishLira = "949";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // ISO 4217 sayısal -> harf kodu; listede olmayan kod olduğu gibi yazılır.
    private static readonly Dictionary<string, string> Currencies = new()
    {
        ["949"] = "TRY", ["840"] = "USD", ["978"] = "EUR", ["826"] = "GBP", ["756"] = "CHF", ["392"] = "JPY",
        ["036"] = "AUD", ["124"] = "CAD", ["208"] = "DKK", ["578"] = "NOK", ["752"] = "SEK", ["682"] = "SAR"
    };

    public static EmailMessage Compose(TransactionCompleted sale, string to)
    {
        var amount = Money(sale.Amount, sale.CurrencyCode);
        var body = new StringBuilder()
            .AppendLine("Sayın müşterimiz,")
            .AppendLine()
            .AppendLine($"{sale.MaskedCardNumber} numaralı kartınızla yapılan satış işlemi onaylandı.")
            .AppendLine()
            .AppendLine($"Tutar        : {amount}");
        if (sale.CurrencyCode != TurkishLira && sale.LocalAmount is { } lira)
            body.AppendLine($"TL karşılığı : {Money(lira, TurkishLira)}");
        if (!string.IsNullOrWhiteSpace(sale.CardAcceptor))
            body.AppendLine($"İşyeri       : {sale.CardAcceptor}");
        body.AppendLine($"Tarih / saat : {DateTime(sale.TransactionDate, sale.LocalTime)}")
            .AppendLine($"İşlem no     : {sale.TransactionId}")
            .AppendLine()
            .AppendLine("Bu e-posta otomatik olarak gönderilmiştir, lütfen yanıtlamayınız.");

        return new EmailMessage(to, $"Kartınızla {amount} tutarında satış yapıldı", body.ToString());
    }

    private static string Money(decimal value, string currencyCode) =>
        $"{value.ToString("N2", Turkish)} {Currencies.GetValueOrDefault(currencyCode, currencyCode)}";

    // yyyymmdd + hhmmss -> 15.03.2026 12:59:59; beklenmeyen formatta olduğu gibi yazılır.
    private static string DateTime(string date, string time) =>
        date.Length == 8 && time.Length == 6
            ? $"{date[6..8]}.{date[4..6]}.{date[..4]} {time[..2]}:{time[2..4]}:{time[4..6]}"
            : $"{date} {time}".Trim();
}
