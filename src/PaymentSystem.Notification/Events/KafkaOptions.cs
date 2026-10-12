namespace PaymentSystem.Notification.Events;

// appsettings.json -> "Kafka" bölümü (ya da Kafka__BootstrapServers gibi ortam değişkenleri).
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9094";

    public string Topic { get; set; } = "payment.transaction-events";

    // Her tüketici servis kendi group'unu kullanır; böylece aynı topic'i başka servisler de baştan sona okuyabilir.
    public string GroupId { get; set; } = "notification";
}
