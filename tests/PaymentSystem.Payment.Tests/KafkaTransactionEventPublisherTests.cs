using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using PaymentSystem.Payment.Events;
using Testcontainers.Kafka;

namespace PaymentSystem.Payment.Tests;

// Testler için gerçek bir Kafka (KRaft, docker-compose'dakiyle aynı imaj) başlatır. (Docker gerektirir.)
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder("apache/kafka:3.9.0").Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public class KafkaTransactionEventPublisherTests(KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private readonly ListLogger<KafkaTransactionEventPublisher> _logger = new();

    private KafkaTransactionEventPublisher Publisher(string topic, string? bootstrapServers = null, int messageTimeoutMs = 30_000) =>
        new(Options.Create(new KafkaOptions
        {
            BootstrapServers = bootstrapServers ?? kafka.BootstrapServers,
            Topic = topic,
            MessageTimeoutMs = messageTimeoutMs
        }), _logger);

    private static TransactionCompleted Approved(long? accountNo = 1000000001) => new(
        Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero),
        "0200", "Sale", "00", "0000", "411111********11", accountNo, "musteri@example.com", 10m, "840", 492.15m,
        "PAYMENT SYSTEM POS SIMULATOR", "20260315", "125959", "TERM0001", "000001");

    private ConsumeResult<string?, string> ConsumeOne(string topic)
    {
        using var consumer = new ConsumerBuilder<string?, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = $"test-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe(topic);
        return consumer.Consume(TimeSpan.FromSeconds(30)) ?? throw new TimeoutException($"no message on {topic}");
    }

    [Fact]
    public void Publish_WritesCamelCaseJsonKeyedByAccount()
    {
        var topic = $"events-{Guid.NewGuid():N}";
        using (var publisher = Publisher(topic))
            publisher.Publish(Approved());      // Dispose bekleyen mesajı gönderir (Flush)

        var message = ConsumeOne(topic);

        Assert.Equal("1000000001", message.Message.Key);
        using var json = JsonDocument.Parse(message.Message.Value);
        var root = json.RootElement;
        Assert.Equal("TransactionCompleted", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", root.GetProperty("transactionId").GetString());
        Assert.Equal("musteri@example.com", root.GetProperty("email").GetString());
        Assert.Equal(492.15m, root.GetProperty("localAmount").GetDecimal());
        Assert.Equal("411111********11", root.GetProperty("maskedCardNumber").GetString());

        Assert.Contains(_logger.Messages, m => m.StartsWith("Publish: event for 0f8fad5b-d9cb-469f-a165-70867728950e F39 00 delivered to"));
    }

    [Fact]
    public void Publish_WithoutAccount_HasNullKey()
    {
        var topic = $"events-{Guid.NewGuid():N}";
        using (var publisher = Publisher(topic))
            publisher.Publish(Approved(accountNo: null));

        Assert.Null(ConsumeOne(topic).Message.Key);
    }

    [Fact]
    public void BrokerUnreachable_LogsUndeliveredEventWithoutThrowing()
    {
        using (var publisher = Publisher("events", bootstrapServers: "127.0.0.1:1", messageTimeoutMs: 1000))
            publisher.Publish(Approved());      // bloklamaz, hata fırlatmaz

        Assert.Contains(_logger.Messages, m => m.StartsWith("Publish: event for 0f8fad5b-d9cb-469f-a165-70867728950e F39 00 was not delivered"));
    }
}
