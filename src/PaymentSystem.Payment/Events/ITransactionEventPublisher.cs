namespace PaymentSystem.Payment.Events;

// İşlem olaylarını yayımlar. İşlem akışını asla bekletmez ve hata fırlatmaz: olay yazılamazsa sadece loglanır,
// işlemin kararı (ve Gate'e dönen cevap) bundan etkilenmez.
public interface ITransactionEventPublisher
{
    void Publish(TransactionCompleted transaction);
}
