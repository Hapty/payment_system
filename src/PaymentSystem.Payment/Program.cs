using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Transactions;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

// Tek satırlık, zaman damgalı konsol logu: her işlem adımı "{Method}: ..." ile başlar, kart numarası hep maskelidir.
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

// sa şifresi appsettings'te durmaz: MSSQL_SA_PASSWORD ortam değişkeninden gelir (.env ile aynı anahtar).
var connectionString = new SqlConnectionStringBuilder(builder.Configuration.GetConnectionString("PaymentSystem"))
{
    Password = builder.Configuration["MSSQL_SA_PASSWORD"]
        ?? throw new InvalidOperationException("MSSQL_SA_PASSWORD is not configured.")
}.ConnectionString;

builder.Services.AddSingleton(new SqlConnectionFactory(connectionString));
builder.Services.AddSingleton<BankAccountRepository>();
builder.Services.AddSingleton<CardRepository>();
builder.Services.AddSingleton<TransactionTypeRepository>();
builder.Services.AddSingleton<MtiProcessingCodeRepository>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TransactionProcessor>();

// İşlemler HTTP'den değil, Gate'in RabbitMQ'ya yazdığı kuyruktan gelir (request-reply). Şifre RABBITMQ_PASSWORD'den gelir.
builder.Services.AddSingleton<IConnectionFactory>(new ConnectionFactory
{
    Uri = new Uri(builder.Configuration.GetConnectionString("RabbitMq")
        ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is not configured.")),
    Password = builder.Configuration["RABBITMQ_PASSWORD"]
        ?? throw new InvalidOperationException("RABBITMQ_PASSWORD is not configured."),
    // Kopan bağlantıyı TransactionQueueConsumer kendisi yeniler; kütüphanenin otomatik toparlanmasıyla çakışmasın.
    AutomaticRecoveryEnabled = false,
    ConsumerDispatchConcurrency = TransactionQueueConsumer.Concurrency
});
builder.Services.AddHostedService<TransactionQueueConsumer>();

// Döviz kurları: arka plan servisi TCMB'den çekip in-memory cache'e yazar, işlem akışı sadece cache'ten okur.
builder.Services.Configure<ExchangeRateOptions>(builder.Configuration.GetSection(ExchangeRateOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ExchangeRateCache>();
builder.Services
    .AddHttpClient<TcmbClient>(client => client.Timeout = TimeSpan.FromSeconds(10))
    // Bu client, singleton refresher yaşadığı sürece yaşar; bağlantıları handler'ın kendisi yeniler (Gate'teki gibi).
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddHostedService<ExchangeRateRefresher>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
