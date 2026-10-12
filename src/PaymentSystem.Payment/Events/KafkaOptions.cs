namespace PaymentSystem.Payment.Events;

// appsettings.json -> "Kafka" bölümü (ya da Kafka__BootstrapServers gibi ortam değişkenleri).
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9094";

    // Payment'ta ne olduğunu anlatan genel olay topic'i; her tüketici kendi consumer group'uyla okur.
    public string Topic { get; set; } = "payment.transaction-events";

    // Kafka'ya bu süre içinde yazılamayan olay bırakılır ve hata loglanır.
    public int MessageTimeoutMs { get; set; } = 30_000;
}
