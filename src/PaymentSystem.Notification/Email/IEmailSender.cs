namespace PaymentSystem.Notification.Email;

public sealed record EmailMessage(string To, string Subject, string Body);

// E-postayı gönderir; gönderemezse exception fırlatır (yeniden denemeyi TransactionNotifier yapar).
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
