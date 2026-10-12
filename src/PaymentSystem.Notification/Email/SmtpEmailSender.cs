using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace PaymentSystem.Notification.Email;

// MailKit ile SMTP'den gönderir (Gmail: smtp.gmail.com:587, STARTTLS, uygulama şifresi).
// Bildirim sayısı düşük olduğu için her e-postada bağlanılır; açık bağlantı tutulmaz, kopma derdi yoktur.
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    private readonly SmtpOptions _smtp = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = new MimeMessage
        {
            Subject = message.Subject,
            Body = new TextPart("plain") { Text = message.Body }
        };
        mime.From.Add(new MailboxAddress(_smtp.FromName, _smtp.Sender!));     // Program.cs açılışta doğrular
        mime.To.Add(MailboxAddress.Parse(message.To));

        using var client = new SmtpClient();
        await client.ConnectAsync(_smtp.Host, _smtp.Port, _smtp.Security, cancellationToken);
        if (!string.IsNullOrEmpty(_smtp.User))
            await client.AuthenticateAsync(_smtp.User, _smtp.Password ?? "", cancellationToken);
        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
