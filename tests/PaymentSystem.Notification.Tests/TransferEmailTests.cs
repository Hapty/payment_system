using PaymentSystem.Notification.Email;

namespace PaymentSystem.Notification.Tests;

public class TransferEmailTests
{
    [Fact]
    public void SenderEmail_ListsBothMaskedCardsAmountDateAndTransactionId()
    {
        var email = TransferEmail.ComposeForSender(TestEvents.ApprovedTransfer(), "gonderen@example.com");

        Assert.Equal("gonderen@example.com", email.To);
        Assert.Equal("Kartınızdan 150,00 TRY tutarında para transferi yapıldı", email.Subject);
        Assert.Contains("540668********01 numaralı kartınızdan 540668********02 numaralı karta 150,00 TRY gönderildi.", email.Body);
        Assert.Contains("Tutar        : 150,00 TRY", email.Body);
        Assert.Contains("Tarih / saat : 15.03.2026 12:59:59", email.Body);
        Assert.Contains($"İşlem no     : {TestEvents.Id}", email.Body);
        Assert.DoesNotContain("TL karşılığı", email.Body);
        Assert.DoesNotContain("İşyeri", email.Body);
    }

    [Fact]
    public void RecipientEmail_SaysWhereTheMoneyCameFrom()
    {
        var email = TransferEmail.ComposeForRecipient(TestEvents.ApprovedTransfer(), "alici@example.com");

        Assert.Equal("alici@example.com", email.To);
        Assert.Equal("Kartınıza 150,00 TRY tutarında para transferi geldi", email.Subject);
        Assert.Contains("540668********02 numaralı kartınıza 540668********01 numaralı karttan 150,00 TRY geldi.", email.Body);
        Assert.Contains($"İşlem no     : {TestEvents.Id}", email.Body);
    }

    [Fact]
    public void ForeignCurrencyTransfer_AlsoShowsLiraEquivalent()
    {
        var transfer = TestEvents.ApprovedTransfer() with { Amount = 10m, CurrencyCode = "840", LocalAmount = 492.15m };

        var email = TransferEmail.ComposeForRecipient(transfer, "alici@example.com");

        Assert.Equal("Kartınıza 10,00 USD tutarında para transferi geldi", email.Subject);
        Assert.Contains("TL karşılığı : 492,15 TRY", email.Body);
    }
}
