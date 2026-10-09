namespace PaymentSystem.Payment.ExchangeRates;

// appsettings.json'daki "ExchangeRates" bölümünden ya da ExchangeRates__RefreshMinutes gibi ortam değişkenlerinden doldurulur.
public class ExchangeRateOptions
{
    public const string SectionName = "ExchangeRates";

    public string TcmbUrl { get; set; } = "https://www.tcmb.gov.tr/kurlar/today.xml";

    // Başarılı bir çekimden sonra bir sonrakine kadar beklenen süre.
    public int RefreshMinutes { get; set; } = 60;

    // Çekim başarısız olursa (TCMB'ye ulaşılamadı, XML bozuk) tekrar denemeden önce beklenen süre.
    public int RetryMinutes { get; set; } = 1;

    // Son başarılı çekimden bu kadar saat geçtiyse kur bayat sayılır ve döviz işlemleri 91 ile reddedilir.
    // Bülten tarihine değil çekim zamanına bakılır: TCMB hafta sonu / bayramda bülten yayımlamaz, o günlerde
    // en son bülten resmî kurdur ve çekim başarılı olduğu sürece geçerlidir.
    public int MaxAgeHours { get; set; } = 24;
}
