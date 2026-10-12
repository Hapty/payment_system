using PaymentSystem.Gate;
using PaymentSystem.Gate.Payment;

var builder = WebApplication.CreateBuilder(args);

// Tek satırlık, zaman damgalı konsol logu: her işlem adımı "{Method}: ..." ile başlar, kart numarası hep maskelidir.
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});

// RabbitMQ şifresi appsettings'te durmaz: RABBITMQ_PASSWORD ortam değişkeninden gelir (.env ile aynı anahtar).
var rabbitMqPassword = builder.Configuration["RABBITMQ_PASSWORD"]
    ?? throw new InvalidOperationException("RABBITMQ_PASSWORD is not configured.");

builder.Services.Configure<GateOptions>(builder.Configuration.GetSection(GateOptions.SectionName));
builder.Services.PostConfigure<GateOptions>(options => options.RabbitMqPassword = rabbitMqPassword);

builder.Services.AddSingleton<IPaymentClient, RabbitMqPaymentClient>();
builder.Services.AddSingleton<TransactionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/api/transactions", TransactionEndpoint.HandleAsync);

app.Run();
