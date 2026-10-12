using MailKit.Security;

namespace PaymentSystem.Notification.Email;

// appsettings.json -> "Smtp" bölümü. Kullanıcı ve şifre appsettings'te durmaz: docker-compose .env'deki
// SMTP_USER / SMTP_PASSWORD'ü Smtp__User / Smtp__Password olarak verir. Varsayılanlar Gmail içindir.
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "smtp.gmail.com";

    public int Port { get; set; } = 587;

    // Gmail 587'de STARTTLS ister. Şifresiz test sunucusunda (Mailpit) None.
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    public string? User { get; set; }

    // Gmail'de hesap şifresi değil, 16 haneli "uygulama şifresi".
    public string? Password { get; set; }

    // Boşsa User kullanılır (Gmail gönderen adresi giriş yapılan hesap olmalı).
    public string? From { get; set; }

    public string FromName { get; set; } = "Payment System";

    // Gönderilemeyen e-posta bu kadar denenir, sonra bırakılır (Kafka'da takılı kalınmaz).
    public int MaxAttempts { get; set; } = 3;

    public double RetryDelaySeconds { get; set; } = 5;

    public string? Sender => string.IsNullOrWhiteSpace(From) ? User : From;
}
