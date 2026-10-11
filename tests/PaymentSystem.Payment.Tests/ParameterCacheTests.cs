using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Handlers;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Parameters;

namespace PaymentSystem.Payment.Tests;

// Testlerde ParameterCache kurulumu: gerçek DB'den ya da sahte repository'den, varsayılan 10 dakikalık yenileme ile.
internal static class TestParameters
{
    public static ParameterCache Create(SqlConnectionFactory db, TimeProvider? clock = null) =>
        Create(new ParameterRepository(db), clock);

    public static ParameterCache Create(IParameterRepository repository, TimeProvider? clock = null, ListLogger<ParameterCache>? logger = null) =>
        new(repository, new MemoryCache(new MemoryCacheOptions()), clock ?? TimeProvider.System,
            Options.Create(new ParameterOptions()), (ILogger<ParameterCache>?)logger ?? NullLogger<ParameterCache>.Instance);
}

// Kaç kez yüklendiğini sayan sahte tanım repository'si; tanımlar test sırasında değiştirilebilir.
internal sealed class FakeParameterRepository : IParameterRepository
{
    public IReadOnlyList<TransactionRoute> Routes { get; set; } = [];
    public IReadOnlyList<EntryMode> EntryModes { get; set; } = [];
    public int Loads { get; private set; }

    public Task<IReadOnlyList<TransactionRoute>> GetRoutesAsync()
    {
        Loads++;
        return Task.FromResult(Routes);
    }

    public Task<IReadOnlyList<EntryMode>> GetEntryModesAsync() => Task.FromResult(EntryModes);
}

public class ParameterCacheTests
{
    private sealed class MovableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly TransactionRoute Sale = new("0200", "000000", "10", "11", "Sale");
    private static readonly EntryMode Chip = new("05", "Chip", false);

    private readonly MovableClock _clock = new();
    private readonly FakeParameterRepository _repository = new() { Routes = [Sale], EntryModes = [Chip] };

    [Fact]
    public async Task LoadsOnce_ThenServesFromMemory()
    {
        var logger = new ListLogger<ParameterCache>();
        var cache = TestParameters.Create(_repository, _clock, logger);

        Assert.Equal(new RoutedType("10", "11", "Sale"), await cache.FindTypeAsync("0200", "000000"));
        Assert.Equal(Chip, await cache.FindEntryModeAsync("05"));
        Assert.Null(await cache.FindTypeAsync("0200", "999999"));     // tanımsız kod DB'ye gitmez
        Assert.Null(await cache.FindEntryModeAsync("99"));

        Assert.Equal(1, _repository.Loads);
        Assert.Contains(logger.Messages, m => m == "GetAsync: loaded 1 transaction routes and 1 entry modes, next refresh in 10 min");
    }

    [Fact]
    public async Task ReloadsAfterTenMinutes()
    {
        var cache = TestParameters.Create(_repository, _clock);
        await cache.FindEntryModeAsync("05");

        _repository.EntryModes = [Chip, new EntryMode("07", "ContactlessChip", true)];
        _clock.Now += TimeSpan.FromMinutes(9);
        Assert.Null(await cache.FindEntryModeAsync("07"));            // 10 dakika dolmadı: eski tanımlar
        Assert.Equal(1, _repository.Loads);

        _clock.Now += TimeSpan.FromMinutes(1);
        Assert.True((await cache.FindEntryModeAsync("07"))!.IsContactless);
        Assert.Equal(2, _repository.Loads);
    }

    [Fact]
    public async Task Invalidate_ReloadsOnNextLookup()
    {
        var logger = new ListLogger<ParameterCache>();
        var cache = TestParameters.Create(_repository, _clock, logger);
        await cache.FindTypeAsync("0200", "000000");

        _repository.Routes = [];
        cache.Invalidate();

        Assert.Null(await cache.FindTypeAsync("0200", "000000"));
        Assert.Equal(2, _repository.Loads);
        Assert.Contains(logger.Messages, m => m.StartsWith("Invalidate: parameter cache cleared"));
    }

    [Fact]
    public async Task ConcurrentLookups_LoadOnce()
    {
        var cache = TestParameters.Create(_repository, _clock);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.FindTypeAsync("0200", "000000")));

        Assert.Equal(1, _repository.Loads);
    }
}

// CRUD ile tanım değişince cache hemen boşaltılır: işlem akışı 10 dakika beklemeden yeni tanımı görür.
public class ParameterCacheInvalidationTests(SqlServerFixture fixture) : DatabaseTest(fixture)
{
    private ParameterCache Parameters { get; } = TestParameters.Create(fixture.Db);

    [Fact]
    public async Task CreatingMapping_IsSeenByCacheImmediately()
    {
        await using (var connection = Db.Create())
            await connection.ExecuteAsync("INSERT INTO dbo.TransactionType (Otc, Ots, Name) VALUES ('10', '11', 'Sale')");
        Assert.Null(await Parameters.FindTypeAsync("0200", "000000"));    // cache yüklendi, eşleme yok

        var controller = new MtiProcessingCodesController(
            new MtiProcessingCodeHandler(new MtiProcessingCodeRepository(Db), Parameters, NullLogger<MtiProcessingCodeHandler>.Instance));
        var created = await controller.Create(new MtiProcessingCode { Mti = "0200", F3_ProcessingCode = "000000", Otc = "10", Ots = "11" });
        Assert.IsType<CreatedAtActionResult>(created.Result);

        Assert.Equal(new RoutedType("10", "11", "Sale"), await Parameters.FindTypeAsync("0200", "000000"));
    }

    [Fact]
    public async Task RenamingAndDeletingType_IsSeenByCacheImmediately()
    {
        await using (var connection = Db.Create())
            await connection.ExecuteAsync(
                """
                INSERT INTO dbo.TransactionType (Otc, Ots, Name) VALUES ('10', '11', 'Sale');
                INSERT INTO dbo.MtiProcessingCode (Mti, F3_ProcessingCode, Otc, Ots) VALUES ('0200', '000000', '10', '11');
                """);
        Assert.Equal("Sale", (await Parameters.FindTypeAsync("0200", "000000"))!.Name);

        var handler = new TransactionTypeHandler(new TransactionTypeRepository(Db), Parameters, NullLogger<TransactionTypeHandler>.Instance);
        Assert.Equal(WriteResult.Ok, await handler.UpdateNameAsync("10", "11", "Purchase"));
        Assert.Equal("Purchase", (await Parameters.FindTypeAsync("0200", "000000"))!.Name);

        Assert.True(await handler.DeleteAsync("10", "11"));
        Assert.Null(await Parameters.FindTypeAsync("0200", "000000"));
    }
}
