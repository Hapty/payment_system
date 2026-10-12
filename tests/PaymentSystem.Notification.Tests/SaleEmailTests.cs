using PaymentSystem.Notification.Email;

namespace PaymentSystem.Notification.Tests;

public class SaleEmailTests
{
    [Fact]
    public void LiraSale_ListsAmountMerchantDateAndTransactionId()
    {
        var email = SaleEmail.Compose(TestEvents.ApprovedSale(), "musteri@example.com");

        Assert.Equal("musteri@example.com", email.To);
        Assert.Equal("Kartınızla 1.500,00 TRY tutarında satış yapıldı", email.Subject);
        Assert.Contains("411111********11 numaralı kartınızla yapılan satış işlemi onaylandı.", email.Body);
        Assert.Contains("Tutar        : 1.500,00 TRY", email.Body);
        Assert.Contains("İşyeri       : PAYMENT SYSTEM POS SIMULATOR", email.Body);
        Assert.Contains("Tarih / saat : 15.03.2026 12:59:59", email.Body);
        Assert.Contains($"İşlem no     : {TestEvents.Id}", email.Body);
        Assert.DoesNotContain("TL karşılığı", email.Body);      // TL işlemde tutarın kendisi zaten TL
    }

    [Fact]
    public void ForeignCurrencySale_AlsoShowsLiraEquivalent()
    {
        var sale = TestEvents.ApprovedSale() with { Amount = 10m, CurrencyCode = "840", LocalAmount = 492.15m };

        var email = SaleEmail.Compose(sale, "musteri@example.com");

        Assert.Equal("Kartınızla 10,00 USD tutarında satış yapıldı", email.Subject);
        Assert.Contains("TL karşılığı : 492,15 TRY", email.Body);
    }

    [Fact]
    public void MissingMerchantAndUnknownCurrency_AreHandled()
    {
        var sale = TestEvents.ApprovedSale() with { CardAcceptor = null, CurrencyCode = "999", LocalAmount = null, TransactionDate = "0315" };

        var email = SaleEmail.Compose(sale, "musteri@example.com");

        Assert.Contains("Tutar        : 1.500,00 999", email.Body);
        Assert.DoesNotContain("İşyeri", email.Body);
        Assert.DoesNotContain("TL karşılığı", email.Body);
        Assert.Contains("Tarih / saat : 0315 125959", email.Body);     // beklenmeyen format olduğu gibi yazılır
    }
}
