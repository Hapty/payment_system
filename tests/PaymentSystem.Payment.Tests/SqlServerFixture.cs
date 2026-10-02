using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Data;
using Testcontainers.MsSql;

namespace PaymentSystem.Payment.Tests;

// Testler için gerçek bir SQL Server container'ı başlatır ve içinde repodaki docker/sql/init scriptlerini çalıştırır.
// Böylece Dapper sorguları, docker-compose'daki DB ile birebir aynı şemaya karşı test edilir. (Docker gerektirir.)
public sealed class SqlServerFixture : IAsyncLifetime
{
    // docker-compose.yml'deki db servisiyle aynı imaj.
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public SqlConnectionFactory Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var initDir = Path.Combine(FindRepoRoot(), "docker", "sql", "init");
        foreach (var script in Directory.GetFiles(initDir, "*.sql").Order())
        {
            var result = await _container.ExecScriptAsync(await File.ReadAllTextAsync(script));
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{Path.GetFileName(script)} failed: {result.Stderr}{result.Stdout}");
        }

        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "PaymentSystem"
        }.ConnectionString;
        Db = new SqlConnectionFactory(connectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // Test dll'i bin/... altında çalışır; PaymentSystem.sln'i bulana kadar yukarı çıkarak repo kökünü buluruz.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaymentSystem.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("PaymentSystem.sln not found above the test directory.");
    }
}

// Bu koleksiyondaki tüm test sınıfları tek bir container'ı paylaşır ve sırayla (paralel olmadan) çalışır.
[CollectionDefinition(nameof(SqlServerCollection))]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

// Her testten önce tabloları boşaltır; her test kendi verisini kendisi ekler.
[Collection(nameof(SqlServerCollection))]
public abstract class DatabaseTest(SqlServerFixture fixture) : IAsyncLifetime
{
    protected SqlConnectionFactory Db => fixture.Db;

    public async Task InitializeAsync()
    {
        await using var connection = Db.Create();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.Card; DELETE FROM dbo.BankAccount; DELETE FROM dbo.TransactionType; DELETE FROM dbo.MtiProcessingCode;");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
