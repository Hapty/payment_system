using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Transactions;

var builder = WebApplication.CreateBuilder(args);

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
