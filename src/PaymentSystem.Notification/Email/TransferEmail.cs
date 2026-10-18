using System.Text;
using PaymentSystem.Notification.Events;

namespace PaymentSystem.Notification.Email;

// Onaylı karttan karta transferin iki e-postası: gönderene (F2'nin hesabı) ve alıcıya (F48'in hesabı).
// Kart numaraları olaydan zaten maskeli gelir.
public static class TransferEmail
{
    public static EmailMessage ComposeForSender(TransactionCompleted transfer, string to)
    {
        var amount = EmailFormat.Amount(transfer);
        return new EmailMessage(to, $"Kartınızdan {amount} tutarında para transferi yapıldı", Body(transfer,
            $"{transfer.MaskedCardNumber} numaralı kartınızdan {transfer.RecipientMaskedCardNumber} numaralı karta {amount} gönderildi."));
    }

    public static EmailMessage ComposeForRecipient(TransactionCompleted transfer, string to)
    {
        var amount = EmailFormat.Amount(transfer);
        return new EmailMessage(to, $"Kartınıza {amount} tutarında para transferi geldi", Body(transfer,
            $"{transfer.RecipientMaskedCardNumber} numaralı kartınıza {transfer.MaskedCardNumber} numaralı karttan {amount} geldi."));
    }

    private static string Body(TransactionCompleted transfer, string summary) =>
        new StringBuilder()
            .AppendLine("Sayın müşterimiz,")
            .AppendLine()
            .AppendLine(summary)
            .AppendLine()
            .AppendAmount(transfer)
            .AppendDateAndId(transfer)
            .AppendLine()
            .AppendLine(EmailFormat.Footer)
            .ToString();
}
