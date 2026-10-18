using Microsoft.Extensions.Options;
using PaymentSystem.Notification.Email;

namespace PaymentSystem.Notification.Tests;

public class TransactionNotifierTests
{
    private readonly FakeEmailSender _sender = new();
    private readonly ListLogger<TransactionNotifier> _logger = new();

    private TransactionNotifier Notifier(int maxAttempts = 3) => new(
        _sender, Options.Create(new SmtpOptions { User = "gonderen@gmail.com", MaxAttempts = maxAttempts, RetryDelaySeconds = 0 }),
        TimeProvider.System, _logger);

    [Fact]
    public async Task ApprovedSale_IsEmailedToAccountAddress()
    {
        Assert.Equal([NotificationResult.Sent], await Notifier().HandleAsync(TestEvents.ApprovedSale(), CancellationToken.None));

        var email = Assert.Single(_sender.Sent);
        Assert.Equal("musteri@example.com", email.To);
        Assert.Equal("Kartınızla 1.500,00 TRY tutarında satış yapıldı", email.Subject);
        Assert.Contains(_logger.Messages, m => m.StartsWith($"HandleAsync: {TestEvents.Id} PAN 411111********11 account 1000000001 emailed (attempt 1)"));
    }

    [Theory]
    [InlineData("TransactionCompleted", "Sale", "51")]              // red
    [InlineData("TransactionCompleted", "BalanceInquiry", "00")]    // onaylı ama satış değil
    [InlineData("TransactionReversed", "Sale", "00")]               // bilinmeyen olay tipi
    public async Task NonApprovedSale_IsIgnored(string eventType, string transactionType, string responseCode)
    {
        var transaction = TestEvents.ApprovedSale() with { EventType = eventType, TransactionType = transactionType, ResponseCode = responseCode };

        Assert.Equal([NotificationResult.Ignored], await Notifier().HandleAsync(transaction, CancellationToken.None));
        Assert.Empty(_sender.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task AccountWithoutEmail_IsNotNotified(string? email)
    {
        Assert.Equal([NotificationResult.NoEmail], await Notifier().HandleAsync(TestEvents.ApprovedSale(email: email), CancellationToken.None));

        Assert.Empty(_sender.Sent);
        Assert.Contains(_logger.Messages, m => m == $"HandleAsync: {TestEvents.Id} account 1000000001 has no email - not notified");
    }

    [Fact]
    public async Task DuplicateEvent_IsEmailedOnce()
    {
        var notifier = Notifier();

        Assert.Equal([NotificationResult.Sent], await notifier.HandleAsync(TestEvents.ApprovedSale(), CancellationToken.None));
        Assert.Equal([NotificationResult.Duplicate], await notifier.HandleAsync(TestEvents.ApprovedSale(), CancellationToken.None));
        Assert.Equal([NotificationResult.Sent], await notifier.HandleAsync(TestEvents.ApprovedSale(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(2, _sender.Sent.Count);
    }

    [Fact]
    public async Task SendFailure_IsRetried()
    {
        _sender.FailTimes = 2;

        Assert.Equal([NotificationResult.Sent], await Notifier(maxAttempts: 3).HandleAsync(TestEvents.ApprovedSale(), CancellationToken.None));

        Assert.Equal(3, _sender.Attempts);
        Assert.Single(_sender.Sent);
        Assert.Contains(_logger.Messages, m => m == $"HandleAsync: {TestEvents.Id} email attempt 1 failed - SMTP unavailable; retrying");
    }

    [Fact]
    public async Task SendFailure_GivesUpAfterMaxAttemptsAndCanBeRetriedLater()
    {
        _sender.FailTimes = 2;
        var notifier = Notifier(maxAttempts: 2);

        Assert.Equal([NotificationResult.Failed], await notifier.HandleAsync(TestEvents.ApprovedSale(), CancellationToken.None));
        Assert.Contains(_logger.Messages, m => m == $"HandleAsync: {TestEvents.Id} account 1000000001 could not be emailed after 2 attempts - giving up");

        // Gönderilemeyen işlem "gönderildi" sayılmaz: aynı olay tekrar gelirse yeniden denenir.
        Assert.Equal([NotificationResult.Sent], await notifier.HandleAsync(TestEvents.ApprovedSale(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_StopsRetrying()
    {
        _sender.FailTimes = 10;
        var notifier = new TransactionNotifier(
            _sender, Options.Create(new SmtpOptions { MaxAttempts = 10, RetryDelaySeconds = 60 }), TimeProvider.System, _logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notifier.HandleAsync(TestEvents.ApprovedSale(), cts.Token));
        Assert.Equal(1, _sender.Attempts);
    }

    // ---------- Karttan karta transfer ----------

    [Fact]
    public async Task ApprovedTransfer_IsEmailedToSenderAndRecipient()
    {
        Assert.Equal([NotificationResult.Sent, NotificationResult.Sent],
            await Notifier().HandleAsync(TestEvents.ApprovedTransfer(), CancellationToken.None));

        Assert.Equal(
            [("gonderen@example.com", "Kartınızdan 150,00 TRY tutarında para transferi yapıldı"),
             ("alici@example.com", "Kartınıza 150,00 TRY tutarında para transferi geldi")],
            _sender.Sent.Select(e => (e.To, e.Subject)));
        Assert.Contains(_logger.Messages, m => m.StartsWith($"HandleAsync: {TestEvents.Id} PAN 540668********01 account 1000000001 emailed"));
        Assert.Contains(_logger.Messages, m => m.StartsWith($"HandleAsync: {TestEvents.Id} PAN 540668********02 account 1000000002 emailed"));
    }

    [Fact]
    public async Task DeclinedTransfer_IsIgnored()
    {
        var transfer = TestEvents.ApprovedTransfer() with { ResponseCode = "51", Irc = "3002" };

        Assert.Equal([NotificationResult.Ignored], await Notifier().HandleAsync(transfer, CancellationToken.None));
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Transfer_RecipientWithoutEmail_OnlySenderIsEmailed()
    {
        Assert.Equal([NotificationResult.Sent, NotificationResult.NoEmail],
            await Notifier().HandleAsync(TestEvents.ApprovedTransfer(recipientEmail: null), CancellationToken.None));

        Assert.Equal("gonderen@example.com", Assert.Single(_sender.Sent).To);
        Assert.Contains(_logger.Messages, m => m == $"HandleAsync: {TestEvents.Id} account 1000000002 has no email - not notified");
    }

    [Fact]
    public async Task Transfer_SenderWithoutEmail_OnlyRecipientIsEmailed()
    {
        Assert.Equal([NotificationResult.NoEmail, NotificationResult.Sent],
            await Notifier().HandleAsync(TestEvents.ApprovedTransfer(email: null), CancellationToken.None));

        Assert.Equal("alici@example.com", Assert.Single(_sender.Sent).To);
    }

    [Fact]
    public async Task DuplicateTransferEvent_EmailsEachPartyOnce()
    {
        var notifier = Notifier();

        await notifier.HandleAsync(TestEvents.ApprovedTransfer(), CancellationToken.None);
        Assert.Equal([NotificationResult.Duplicate, NotificationResult.Duplicate],
            await notifier.HandleAsync(TestEvents.ApprovedTransfer(), CancellationToken.None));

        Assert.Equal(2, _sender.Sent.Count);
    }

    [Fact]
    public async Task Transfer_SenderFailure_DoesNotBlockRecipientAndIsRetriedLater()
    {
        _sender.FailTimes = 2;      // gönderenin iki denemesi de düşer
        var notifier = Notifier(maxAttempts: 2);

        Assert.Equal([NotificationResult.Failed, NotificationResult.Sent],
            await notifier.HandleAsync(TestEvents.ApprovedTransfer(), CancellationToken.None));
        Assert.Equal("alici@example.com", Assert.Single(_sender.Sent).To);

        // Aynı olay tekrar gelirse sadece gönderilemeyen e-posta yeniden denenir.
        Assert.Equal([NotificationResult.Sent, NotificationResult.Duplicate],
            await notifier.HandleAsync(TestEvents.ApprovedTransfer(), CancellationToken.None));
        Assert.Equal(["alici@example.com", "gonderen@example.com"], _sender.Sent.Select(e => e.To));
    }

    [Fact]
    public async Task SaleAndTransferRecipient_AreRememberedSeparately()
    {
        var notifier = Notifier();

        // Aynı Guid ile önce satış (kart sahibi) hatırlanır; transferin alıcısı yine de e-posta alır.
        await notifier.HandleAsync(TestEvents.ApprovedSale(), CancellationToken.None);
        Assert.Equal([NotificationResult.Duplicate, NotificationResult.Sent],
            await notifier.HandleAsync(TestEvents.ApprovedTransfer(), CancellationToken.None));
    }

    [Fact]
    public async Task OldestSentIds_AreForgottenBeyondLimit()
    {
        var notifier = Notifier();
        var first = Guid.NewGuid();
        await notifier.HandleAsync(TestEvents.ApprovedSale(first), CancellationToken.None);
        for (var i = 0; i < 10_000; i++)
            await notifier.HandleAsync(TestEvents.ApprovedSale(Guid.NewGuid()), CancellationToken.None);

        // İlk işlem artık hatırlanmıyor; bellek sınırsız büyümez.
        Assert.Equal([NotificationResult.Sent], await notifier.HandleAsync(TestEvents.ApprovedSale(first), CancellationToken.None));
    }
}
