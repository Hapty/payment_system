using Microsoft.OpenApi.Any;
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
// Swagger UI'da "Try it out" denince gövde dolu gelsin: POST /api/transactions'a örnek bir satış isteği eklenir.
builder.Services.AddOpenApi(options => options.AddOperationTransformer((operation, context, _) =>
{
    if (context.Description.RelativePath == "api/transactions"
        && operation.RequestBody?.Content.TryGetValue("application/json", out var body) == true)
    {
        body.Example = new OpenApiObject
        {
            ["mti"] = new OpenApiString("0200"),
            ["F2"] = new OpenApiString("4111111111111111"),
            ["F3"] = new OpenApiString("000000"),
            ["F4"] = new OpenApiLong(15000),
            ["F49"] = new OpenApiString("949"),
            ["F7"] = new OpenApiString("1016095959"),
            ["F11"] = new OpenApiString("000001"),
            ["F12"] = new OpenApiString("125959"),
            ["F13"] = new OpenApiString("20261016"),
            ["F41"] = new OpenApiString("TERM0001"),
            ["F42"] = new OpenApiString("TRENDYOL       "),
            ["F14"] = new OpenApiString("2812"),
            ["F18"] = new OpenApiString("5399"),
            ["F22"] = new OpenApiString("051"),
            ["F43"] = new OpenApiString("TRENDYOL                 ISTANBUL     TR")
        };
    }
    return Task.CompletedTask;
}));

var app = builder.Build();

// OpenAPI şeması (/openapi/v1.json) ve Swagger UI (/swagger) Swagger:Enabled ile açılır; ortamdan (Development) bağımsız.
if (app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Gate v1"));
}

app.MapPost("/api/transactions", TransactionEndpoint.HandleAsync)
    .WithName("CreateTransaction")
    .WithTags("Transactions")
    .WithSummary("Bir finansal isteği (satış, bakiye sorgu, transfer) işler")
    .WithDescription("Alanlar ISO8583 alan numarasıyla gelir (\"F2\", \"F3\" ...). Bozuk ya da eksik alan 400 (ValidationProblem) " +
        "ile döner. Geçerli istek RabbitMQ ile Payment'a iletilir; onay da red de 200 ile döner, karar cevaptaki F39'dadır " +
        "(00 onay, 51 yetersiz bakiye ...).");

app.Run();
