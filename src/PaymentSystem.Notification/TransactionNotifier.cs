using Microsoft.Extensions.Options;
using PaymentSystem.Notification.Email;
using PaymentSystem.Notification.Events;

namespace PaymentSystem.Notification;

public enum NotificationResult
{
    Sent,
    Ignored,        // onaylı satış / transfer değil (red, bakiye sorgu, bilinmeyen olay tipi)
    NoEmail,        // hesabın e-postası yok
    Duplicate,      // bu işlem için bu kişiye e-posta zaten gönderildi
    Failed          // MaxAttempts denemede de gönderilemedi
}

// E-postanın kime gittiği: F2 kartının sahibi (satışta müşteri, transferde gönderen) ya da transferde F48'in sahibi.
public enum Party
{
    Cardholder,
    Recipient
}

// Gelen işlem olayına bakıp gerekiyorsa e-posta atar. Onaylı satış (Sale + F39 00) karta, onaylı karttan karta transfer
// (MoneyTransfer + F39 00) gönderene ve alıcıya bildirilir; her e-posta için ayrı bir sonuç döner.
// Kafka bir olayı birden fazla teslim edebilir: gönderilen (işlem Guid'i, kişi) çiftleri bellekte tutulur, aynı kişiye
// ikinci e-posta gitmez. Bir kişiye gönderilemeyen e-posta diğerini engellemez.
public sealed class TransactionNotifier(
    IEmailSender sender, IOptions<SmtpOptions> options, TimeProvider clock, ILogger<TransactionNotifier> logger)
{
    private const string Sale = "Sale";
    private const string MoneyTransfer = "MoneyTransfer";
    private const string Approved = "00";
    private const int RememberedIds = 10_000;

    private readonly SmtpOptions _smtp = options.Value;
    private readonly HashSet<(Guid, Party)> _sent = [];
    private readonly Queue<(Guid, Party)> _sentOrder = new();

    public async Task<IReadOnlyList<NotificationResult>> HandleAsync(TransactionCompleted transaction, CancellationToken cancellationToken)
    {
        if (transaction.EventType != "TransactionCompleted" || transaction.ResponseCode != Approved
            || transaction.TransactionType is not (Sale or MoneyTransfer))
        {
            logger.LogDebug("{Method}: {Id} {Type} F39 {ResponseCode} ignored",
                nameof(HandleAsync), transaction.TransactionId, transaction.TransactionType, transaction.ResponseCode);
            return [NotificationResult.Ignored];
        }

        var cardholder = await NotifyAsync(transaction, Party.Cardholder, transaction.AccountNo, transaction.MaskedCardNumber,
            transaction.Email, to => transaction.TransactionType == Sale
                ? SaleEmail.Compose(transaction, to)
                : TransferEmail.ComposeForSender(transaction, to), cancellationToken);
        if (transaction.TransactionType == Sale)
            return [cardholder];

        var recipient = await NotifyAsync(transaction, Party.Recipient, transaction.RecipientAccountNo,
            transaction.RecipientMaskedCardNumber, transaction.RecipientEmail,
            to => TransferEmail.ComposeForRecipient(transaction, to), cancellationToken);
        return [cardholder, recipient];
    }

    // Tek bir kişiye e-posta: e-postası yoksa atlanır, daha önce gönderildiyse tekrarlanmaz, hata alırsa MaxAttempts kez denenir.
    private async Task<NotificationResult> NotifyAsync(TransactionCompleted transaction, Party party, long? accountNo, string? maskedPan,
        string? address, Func<string, EmailMessage> compose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            logger.LogInformation("{Method}: {Id} account {AccountNo} has no email - not notified",
                nameof(HandleAsync), transaction.TransactionId, accountNo);
            return NotificationResult.NoEmail;
        }

        var key = (transaction.TransactionId, party);
        if (_sent.Contains(key))
        {
            logger.LogWarning("{Method}: {Id} account {AccountNo} already notified - duplicate event skipped",
                nameof(HandleAsync), transaction.TransactionId, accountNo);
            return NotificationResult.Duplicate;
        }

        var email = compose(address);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await sender.SendAsync(email, cancellationToken);
                Remember(key);
                logger.LogInformation("{Method}: {Id} PAN {Pan} account {AccountNo} emailed (attempt {Attempt})",
                    nameof(HandleAsync), transaction.TransactionId, maskedPan, accountNo, attempt);
                return NotificationResult.Sent;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= _smtp.MaxAttempts)
                {
                    logger.LogError(ex, "{Method}: {Id} account {AccountNo} could not be emailed after {Attempts} attempts - giving up",
                        nameof(HandleAsync), transaction.TransactionId, accountNo, attempt);
                    return NotificationResult.Failed;
                }
                logger.LogWarning("{Method}: {Id} email attempt {Attempt} failed - {Error}; retrying",
                    nameof(HandleAsync), transaction.TransactionId, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(_smtp.RetryDelaySeconds), clock, cancellationToken);
            }
        }
    }

    // En son gönderilen RememberedIds e-posta hatırlanır; bellek sınırsız büyümez.
    private void Remember((Guid, Party) key)
    {
        _sent.Add(key);
        _sentOrder.Enqueue(key);
        if (_sentOrder.Count > RememberedIds)
            _sent.Remove(_sentOrder.Dequeue());
    }
}
