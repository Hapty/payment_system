using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;

namespace PaymentSystem.Notification.Events;

// Payment'ın işlem olaylarını Kafka'dan okur ve TransactionNotifier'a verir. Olaylar sırayla işlenir.
// Offset ancak olay işlendikten sonra saklanır (EnableAutoOffsetStore = false): servis olayın ortasında kapanırsa
// açılınca aynı olay tekrar gelir (at-least-once). Okunamayan ya da gönderilemeyen olay loglanıp geçilir, tüketici takılmaz.
// Kafka ayakta değilse kütüphane arka planda yeniden bağlanmayı kendisi dener.
public sealed class TransactionEventConsumer(
    IOptions<KafkaOptions> options, TransactionNotifier notifier, ILogger<TransactionEventConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly KafkaOptions _kafka = options.Value;
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Partition'lar ilk kez atandığında tamamlanır (testler bunu bekler).
    public Task Listening => _listening.Task;

    // Consume bloklayan bir çağrı: host'un açılışını bekletmesin diye ayrı thread'de çalışır.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => ConsumeAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await EnsureTopicAsync(stoppingToken);

        using var consumer = new ConsumerBuilder<string?, string>(new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = _kafka.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,     // group ilk kez açılıyorsa topic'in başından okur, olay kaçmaz
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false
        })
            .SetErrorHandler((_, error) => logger.LogWarning("{Method}: Kafka {Code} {Reason}", "Kafka", error.Code, error.Reason))
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                logger.LogInformation("{Method}: listening on {Partitions}", nameof(ConsumeAsync), string.Join(", ", partitions));
                _listening.TrySetResult();
            })
            .Build();

        consumer.Subscribe(_kafka.Topic);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string?, string> result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning("{Method}: consume failed - {Reason}", nameof(ConsumeAsync), ex.Error.Reason);
                    continue;
                }

                await HandleAsync(result, stoppingToken);
                consumer.StoreOffset(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();       // saklanan offset'leri commit eder ve group'tan düzgünce çıkar
        }
    }

    // Topic yoksa (Payment henüz hiç olay yazmadıysa) oluşturur; partition sayısı broker'ın varsayılanıdır.
    // Topic'i Payment'ın ilk olayı oluştursaydı, consumer onu ancak bir sonraki metadata yenilemesinde (5 dk) görürdü.
    // Kafka'ya ulaşılamazsa loglanır ve devam edilir: consumer bağlanınca topic'i yine bulur.
    private async Task EnsureTopicAsync(CancellationToken stoppingToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(
                [new TopicSpecification { Name = _kafka.Topic, NumPartitions = -1, ReplicationFactor = -1 }],
                new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(10) })
                .WaitAsync(stoppingToken);
            logger.LogInformation("{Method}: topic {Topic} created", nameof(EnsureTopicAsync), _kafka.Topic);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
        }
        catch (KafkaException ex)
        {
            logger.LogWarning("{Method}: topic {Topic} could not be created - {Reason}", nameof(EnsureTopicAsync), _kafka.Topic, ex.Error.Reason);
        }
    }

    private async Task HandleAsync(ConsumeResult<string?, string> result, CancellationToken stoppingToken)
    {
        TransactionCompleted? transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<TransactionCompleted>(result.Message.Value, Json);
        }
        catch (JsonException ex)
        {
            transaction = null;
            logger.LogWarning("{Method}: {TopicPartitionOffset} is not valid JSON - {Error}", nameof(HandleAsync), result.TopicPartitionOffset, ex.Message);
        }
        if (transaction is null)
        {
            logger.LogWarning("{Method}: {TopicPartitionOffset} skipped - unreadable event", nameof(HandleAsync), result.TopicPartitionOffset);
            return;
        }

        try
        {
            await notifier.HandleAsync(transaction, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Method}: {TopicPartitionOffset} event {Id} failed - skipped",
                nameof(HandleAsync), result.TopicPartitionOffset, transaction.TransactionId);
        }
    }
}
