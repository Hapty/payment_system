using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace PaymentSystem.Payment.ExchangeRates;

// TCMB'nin günlük kur bülteni: bülten tarihi + para birimi (USD, EUR ...) başına 1 birimin TL karşılığı (döviz satış kuru).
public sealed record TcmbBulletin(DateOnly Date, IReadOnlyDictionary<string, decimal> SellingRates);

// TCMB today.xml'ini indirir ve ayrıştırır. Dosyanın biçimi:
//   <Tarih_Date Tarih="08.10.2026" ...>
//     <Currency Kod="USD" ...><Unit>1</Unit> ... <ForexSelling>49.2152</ForexSelling> ...</Currency>
public sealed class TcmbClient(HttpClient http, IOptions<ExchangeRateOptions> options)
{
    public async Task<TcmbBulletin> FetchAsync(CancellationToken cancellationToken) =>
        Parse(await http.GetStringAsync(options.Value.TcmbUrl, cancellationToken));

    // Bozuk XML'de XmlException, tarih ya da kur yoksa FormatException fırlatır.
    public static TcmbBulletin Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root!;
        var dateText = (string?)root.Attribute("Tarih") ?? throw new FormatException("TCMB bulletin has no date.");
        var date = DateOnly.ParseExact(dateText, "dd.MM.yyyy", CultureInfo.InvariantCulture);

        var rates = new Dictionary<string, decimal>();
        foreach (var currency in root.Elements("Currency"))
        {
            // XDR gibi bazı satırlarda döviz satış kuru boş gelir; onlar atlanır.
            if ((string?)currency.Attribute("Kod") is not { Length: 3 } code
                || !decimal.TryParse((string?)currency.Element("ForexSelling"), NumberStyles.Number, CultureInfo.InvariantCulture, out var selling)
                || !int.TryParse((string?)currency.Element("Unit"), CultureInfo.InvariantCulture, out var unit)
                || selling <= 0 || unit <= 0)
                continue;

            rates[code] = selling / unit;   // JPY gibi kurlar 100 birim için yayımlanır
        }

        if (rates.Count == 0)
            throw new FormatException("TCMB bulletin has no forex selling rates.");
        return new TcmbBulletin(date, rates);
    }
}
