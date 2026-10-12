using PaymentSystem.Payment.Events;

namespace PaymentSystem.Payment.Tests;

// Kafka yerine yayımlanan olayları toplar; testler TransactionHandler'ın hangi olayı ürettiğini bununla kontrol eder.
public sealed class RecordingEventPublisher : ITransactionEventPublisher
{
    private readonly List<TransactionCompleted> _events = [];

    public IReadOnlyList<TransactionCompleted> Events
    {
        get { lock (_events) return _events.ToArray(); }
    }

    public void Publish(TransactionCompleted transaction)
    {
        lock (_events) _events.Add(transaction);
    }
}
