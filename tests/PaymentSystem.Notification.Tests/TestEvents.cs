using PaymentSystem.Notification.Email;
using PaymentSystem.Notification.Events;

namespace PaymentSystem.Notification.Tests;

public static class TestEvents
{
    public static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    // Payment'ın onaylı bir TL satış için yazdığı olay.
    public static TransactionCompleted ApprovedSale(Guid? id = null, string? email = "musteri@example.com") => new()
    {
        EventType = "TransactionCompleted",
        Version = 1,
        TransactionId = id ?? Id,
        OccurredAt = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero),
        Mti = "0200",
        TransactionType = "Sale",
        ResponseCode = "00",
        Irc = "0000",
        MaskedCardNumber = "411111********11",
        AccountNo = 1000000001,
        Email = email,
        Amount = 1500m,
        CurrencyCode = "949",
        LocalAmount = 1500m,
        CardAcceptor = "PAYMENT SYSTEM POS SIMULATOR",
        TransactionDate = "20260315",
        LocalTime = "125959",
        TerminalId = "TERM0001",
        Stan = "000001"
    };

    // Payment'ın onaylı bir karttan karta TL transferi için yazdığı olay: gönderen F2, alıcı F48.
    public static TransactionCompleted ApprovedTransfer(
        Guid? id = null, string? email = "gonderen@example.com", string? recipientEmail = "alici@example.com") =>
        ApprovedSale(id, email) with
        {
            TransactionType = "MoneyTransfer",
            MaskedCardNumber = "540668********01",
            Amount = 150m,
            LocalAmount = 150m,
            CardAcceptor = null,
            RecipientMaskedCardNumber = "540668********02",
            RecipientAccountNo = 1000000002,
            RecipientEmail = recipientEmail
        };
}

// SMTP yerine gönderilen e-postaları toplar; ilk FailTimes denemede hata fırlatır.
public sealed class FakeEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public int FailTimes { get; set; }
    public int Attempts { get; private set; }

    public IReadOnlyList<EmailMessage> Sent
    {
        get { lock (_sent) return _sent.ToArray(); }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Attempts++;
        if (Attempts <= FailTimes)
            throw new IOException("SMTP unavailable");
        lock (_sent) _sent.Add(message);
        return Task.CompletedTask;
    }
}
