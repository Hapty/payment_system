using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Data;

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

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
