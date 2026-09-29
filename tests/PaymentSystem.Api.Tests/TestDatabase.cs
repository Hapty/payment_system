using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaymentSystem.Api.Data;

namespace PaymentSystem.Api.Tests;

// Each test class instance gets its own in-memory SQLite database: fast, no Docker needed,
// and unlike the EF InMemory provider it supports ExecuteDeleteAsync and enforces unique indexes.
public abstract class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    protected TestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    // A fresh context per call, so assertions read from the database instead of the change tracker.
    protected PaymentDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentDbContext>().UseSqlite(_connection).Options);

    protected async Task SeedAsync(params object[] entities)
    {
        await using var db = NewContext();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
