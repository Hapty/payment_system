using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentSystem.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// The sa password stays out of appsettings: it comes from the MSSQL_SA_PASSWORD env var (same key as .env) or user-secrets.
var connectionString = new SqlConnectionStringBuilder(builder.Configuration.GetConnectionString("PaymentSystem"))
{
    Password = builder.Configuration["MSSQL_SA_PASSWORD"]
        ?? throw new InvalidOperationException("MSSQL_SA_PASSWORD is not configured.")
}.ConnectionString;

builder.Services.AddDbContext<PaymentDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
