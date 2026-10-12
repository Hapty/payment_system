using Microsoft.Extensions.Options;
using PaymentSystem.Notification.Email;
using PaymentSystem.Notification.Events;

namespace PaymentSystem.Notification;

public enum NotificationResult
{
    Sent,
    Ignored,        // onaylı satış değil (red, bakiye sorgu, bilinmeyen olay tipi)
    NoEmail,        // hesabın e-postası yok
    Duplicate,      // bu işlem için e-posta zaten gönderildi
    Failed          // MaxAttempts denemede de gönderilemedi
}

// Gelen işlem olayına bakıp gerekiyorsa e-posta atar. Sadece onaylı satış (Sale + F39 00) bildirilir.
// Kafka bir olayı birden fazla teslim edebilir: gönderilen işlemlerin Guid'i bellekte tutulur, aynı işleme ikinci e-posta gitmez.
public sealed class TransactionNotifier(
    IEmailSender sender, IOptions<SmtpOptions> options, TimeProvider clock, ILogger<TransactionNotifier> logger)
{
    private const string Sale = "Sale";
    private const string Approved = "00";
    private const int RememberedIds = 10_000;

    private readonly SmtpOptions _smtp = options.Value;
    private readonly HashSet<Guid> _sent = [];
    private readonly Queue<Guid> _sentOrder = new();

    public async Task<NotificationResult> HandleAsync(TransactionCompleted transaction, CancellationToken cancellationToken)
    {
        if (transaction.EventType != "TransactionCompleted" || transaction.TransactionType != Sale || transaction.ResponseCode != Approved)
        {
            logger.LogDebug("{Method}: {Id} {Type} F39 {ResponseCode} ignored",
                nameof(HandleAsync), transaction.TransactionId, transaction.TransactionType, transaction.ResponseCode);
            return NotificationResult.Ignored;
        }

        if (string.IsNullOrWhiteSpace(transaction.Email))
        {
            logger.LogInformation("{Method}: {Id} account {AccountNo} has no email - not notified",
                nameof(HandleAsync), transaction.TransactionId, transaction.AccountNo);
            return NotificationResult.NoEmail;
        }

        if (_sent.Contains(transaction.TransactionId))
        {
            logger.LogWarning("{Method}: {Id} already notified - duplicate event skipped", nameof(HandleAsync), transaction.TransactionId);
            return NotificationResult.Duplicate;
        }

        var email = SaleEmail.Compose(transaction, transaction.Email);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await sender.SendAsync(email, cancellationToken);
                Remember(transaction.TransactionId);
                logger.LogInformation("{Method}: {Id} PAN {Pan} account {AccountNo} emailed (attempt {Attempt})",
                    nameof(HandleAsync), transaction.TransactionId, transaction.MaskedCardNumber, transaction.AccountNo, attempt);
                return NotificationResult.Sent;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= _smtp.MaxAttempts)
                {
                    logger.LogError(ex, "{Method}: {Id} account {AccountNo} could not be emailed after {Attempts} attempts - giving up",
                        nameof(HandleAsync), transaction.TransactionId, transaction.AccountNo, attempt);
                    return NotificationResult.Failed;
                }
                logger.LogWarning("{Method}: {Id} email attempt {Attempt} failed - {Error}; retrying",
                    nameof(HandleAsync), transaction.TransactionId, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(_smtp.RetryDelaySeconds), clock, cancellationToken);
            }
        }
    }

    // En son gönderilen RememberedIds işlem hatırlanır; bellek sınırsız büyümez.
    private void Remember(Guid id)
    {
        _sent.Add(id);
        _sentOrder.Enqueue(id);
        if (_sentOrder.Count > RememberedIds)
            _sent.Remove(_sentOrder.Dequeue());
    }
}
