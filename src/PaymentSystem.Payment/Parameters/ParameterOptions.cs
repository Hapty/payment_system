namespace PaymentSystem.Payment.Parameters;

// appsettings.json'daki "Parameters" bölümünden ya da Parameters__RefreshMinutes ortam değişkeninden doldurulur.
public class ParameterOptions
{
    public const string SectionName = "Parameters";

    // Tanım tabloları cache'e yüklendikten bu kadar dakika sonra ilk ihtiyaçta DB'den yeniden okunur.
    public int RefreshMinutes { get; set; } = 10;
}
