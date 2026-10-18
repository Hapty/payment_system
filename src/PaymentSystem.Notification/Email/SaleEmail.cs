using System.Text;
using PaymentSystem.Notification.Events;

namespace PaymentSystem.Notification.Email;

// Onaylı satışın müşteriye giden e-postası. Kart numarası olaydan zaten maskeli gelir.
public static class SaleEmail
{
    public static EmailMessage Compose(TransactionCompleted sale, string to)
    {
        var amount = EmailFormat.Amount(sale);
        var body = new StringBuilder()
            .AppendLine("Sayın müşterimiz,")
            .AppendLine()
            .AppendLine($"{sale.MaskedCardNumber} numaralı kartınızla yapılan satış işlemi onaylandı.")
            .AppendLine()
            .AppendAmount(sale);
        if (!string.IsNullOrWhiteSpace(sale.CardAcceptor))
            body.AppendLine($"İşyeri       : {sale.CardAcceptor}");
        body.AppendDateAndId(sale)
            .AppendLine()
            .AppendLine(EmailFormat.Footer);

        return new EmailMessage(to, $"Kartınızla {amount} tutarında satış yapıldı", body.ToString());
    }
}
