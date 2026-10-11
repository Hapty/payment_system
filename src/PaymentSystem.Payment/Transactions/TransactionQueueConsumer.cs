using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Text.Json;
using PaymentSystem.Payment.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace PaymentSystem.Payment.Transactions;

// Gate'in RabbitMQ üzerinden gönderdiği işlem isteklerini işler ve cevabı isteğin ReplyTo adresine,
// aynı CorrelationId ile yazar (request-reply). Gate cevabı beklediği için akış dışarıdan bakınca senkron kalır.
// RabbitMQ henüz ayakta değilse ya da bağlantı koparsa RetryDelay sonra yeniden bağlanır.
public sealed class TransactionQueueConsumer(
    IConnectionFactory factory, TransactionProcessor processor, TimeProvider clock, ILogger<TransactionQueueConsumer> logger)
    : BackgroundService
{
    // Gate'teki RabbitMqPaymentClient.QueueName ile aynı olmalı.
    public const string QueueName = "payment.transactions";

    // Aynı anda işlenen en fazla istek sayısı; bağlantının ConsumerDispatchConcurrency'si de buna göre ayarlanır.
    public const ushort Concurrency = 8;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Kuyruk ilk kez dinlenmeye başlandığında tamamlanır (testler bunu bekler).
    public Task Listening => _listening.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilDisconnectedAsync(stoppingToken);
                logger.LogWarning("RabbitMQ connection lost, reconnecting in {Delay}s", RetryDelay.TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is RabbitMQClientException or IOException or SocketException)
            {
                // BrokerUnreachableException da IOException'dır.
                logger.LogWarning("RabbitMQ unavailable ({Error}: {Message}), retrying in {Delay}s",
                    ex.GetType().Name, ex.Message, RetryDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConsumeUntilDisconnectedAsync(CancellationToken stoppingToken)
    {
        await using var connection = await factory.CreateConnectionAsync("payment", stoppingToken);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ConnectionShutdownAsync += (_, _) =>
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        // Kuyruk iki taraf da tanımlar (aynı ayarlarla): hangisi önce açılırsa açılsın kuyruk hazır olur.
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        // RabbitMQ en fazla Concurrency kadar onaylanmamış mesaj verir: elde bekleyip süresi dolan istek birikmesin.
        await channel.BasicQosAsync(0, Concurrency, global: false, stoppingToken);

        // Aynı kanala paralel handler'lardan cevap ve ack yazılır; sırayla yazılsınlar.
        var writeLock = new SemaphoreSlim(1, 1);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, writeLock, delivery, stoppingToken);
        await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, stoppingToken);

        logger.LogInformation("Listening for transactions on RabbitMQ queue {Queue}", QueueName);
        _listening.TrySetResult();
        await disconnected.Task.WaitAsync(stoppingToken);
    }

    private async Task HandleAsync(IChannel channel, SemaphoreSlim writeLock, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        var properties = delivery.BasicProperties;
        var responseCode = await DecideAsync(properties, delivery.Body);

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            if (responseCode is not null && properties.ReplyTo is { } replyTo)
            {
                var reply = new BasicProperties { CorrelationId = properties.CorrelationId, ContentType = "application/json" };
                var body = JsonSerializer.SerializeToUtf8Bytes(new TransactionResponse(responseCode), Json);
                await channel.BasicPublishAsync(string.Empty, replyTo, mandatory: false, reply, body, cancellationToken);
            }

            // Cevap verildikten sonra (ya da süresi dolmuş istek atıldıktan sonra) onaylanır. Mesaj hiçbir durumda
            // kuyruğa geri konmaz: Gate o isteğe ya cevap aldı ya da 91 döndü, aynı işlemi tekrar işlemek anlamsız.
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    // F39 cevap kodunu döner; süresi dolmuş istek için null (işlenmez, cevap da gönderilmez).
    private async Task<string?> DecideAsync(IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body)
    {
        if (IsExpired(properties))
        {
            logger.LogWarning("Dropping expired request {CorrelationId}: the Gate has already answered 91", properties.CorrelationId);
            return null;
        }

        TransactionRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<TransactionRequest>(body.Span, Json);
        }
        catch (JsonException)
        {
            request = null;
        }

        // Alanların ISO8583 formatını Gate zaten kontrol eder; bu, DB kolonlarına sığmayan değerlere karşı son kontrol.
        if (request is null || !Validator.TryValidateObject(request, new ValidationContext(request), null, validateAllProperties: true))
        {
            logger.LogWarning("Invalid transaction message {CorrelationId} - responding 30", properties.CorrelationId);
            return ResponseCodes.FormatError;
        }

        try
        {
            return await processor.ProcessAsync(request);
        }
        catch (Exception ex)
        {
            // Örn. DB'ye ulaşılamıyor. Transaction commit edilmediği için para hareketi yok; Gate'e "şu an yapılamıyor" denir.
            logger.LogError(ex, "Transaction {CorrelationId} (TID {TerminalId} STAN {Stan}) failed - responding 91",
                properties.CorrelationId, request.TerminalId, request.Stan);
            return ResponseCodes.SystemUnavailable;
        }
    }

    // Gate isteği Timestamp (saniye) ve Expiration (ms, Gate'in bekleme süresi) ile gönderir. Süre dolduysa Gate 91 döndü demektir.
    // RabbitMQ süresi dolan mesajı kuyrukta siler; bu kontrol, kuyruktan alınmış ama sırası geç gelmiş mesajlar içindir.
    private bool IsExpired(IReadOnlyBasicProperties properties)
    {
        if (!properties.IsTimestampPresent() || !long.TryParse(properties.Expiration, out var ttlMs))
            return false;
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime);
        return clock.GetUtcNow() - sentAt >= TimeSpan.FromMilliseconds(ttlMs);
    }
}
