using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace PaymentSystem.Payment.Events;

// TransactionCompleted olaylarını Kafka'ya yazar. Produce bloklamaz: mesaj önce kütüphanenin yerel kuyruğuna girer,
// arka planda gönderilir; sonucu (teslim edildi / edilemedi) delivery handler loglar. İşlem akışı Kafka'yı hiç beklemez.
// Kafka kapalıysa olay MessageTimeoutMs boyunca yeniden denenir, sonra bırakılır (outbox yok: bilinçli tercih).
public sealed class KafkaTransactionEventPublisher : ITransactionEventPublisher, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IProducer<string?, string> _producer;
    private readonly string _topic;
    private readonly ILogger<KafkaTransactionEventPublisher> _logger;

    public KafkaTransactionEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaTransactionEventPublisher> logger)
    {
        var settings = options.Value;
        _topic = settings.Topic;
        _logger = logger;
        _producer = new ProducerBuilder<string?, string>(new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,           // yeniden denemede broker'da çift kayıt oluşmaz
            MessageTimeoutMs = settings.MessageTimeoutMs
        })
            .SetErrorHandler((_, error) => _logger.LogWarning("{Method}: Kafka {Code} {Reason}", "Kafka", error.Code, error.Reason))
            .Build();
    }

    // Anahtar hesap no: aynı hesabın olayları aynı partition'a düşer ve sırası korunur.
    public void Publish(TransactionCompleted transaction)
    {
        var message = new Message<string?, string> { Key = transaction.AccountNo?.ToString(), Value = JsonSerializer.Serialize(transaction, Json) };
        try
        {
            _producer.Produce(_topic, message, report => OnDelivered(transaction, report));
        }
        catch (Exception ex)
        {
            // Örn. yerel kuyruk dolu (Kafka uzun süredir kapalı) ya da uygulama kapanıyor.
            _logger.LogError(ex, "{Method}: event for {Id} F39 {ResponseCode} could not be queued for {Topic}",
                nameof(Publish), transaction.TransactionId, transaction.ResponseCode, _topic);
        }
    }

    private void OnDelivered(TransactionCompleted transaction, DeliveryReport<string?, string> report)
    {
        if (report.Error.IsError)
            _logger.LogError("{Method}: event for {Id} F39 {ResponseCode} was not delivered to {Topic} - {Reason}",
                nameof(Publish), transaction.TransactionId, transaction.ResponseCode, _topic, report.Error.Reason);
        else
            _logger.LogInformation("{Method}: event for {Id} F39 {ResponseCode} delivered to {TopicPartitionOffset}",
                nameof(Publish), transaction.TransactionId, transaction.ResponseCode, report.TopicPartitionOffset);
    }

    // Kapanırken yerel kuyrukta bekleyen olaylar için kısa bir süre tanınır.
    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
