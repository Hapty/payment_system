using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PaymentSystem.Payment.Data;

namespace PaymentSystem.Payment.Parameters;

// İşlem akışının tanım tabloları (TransactionType + MtiProcessingCode yönlendirmesi, EntryMode, InternalResponseCode) IMemoryCache'te tek kayıt
// olarak durur: işlem başına DB'ye gidilmez, tanımsız bir kod da DB'ye gitmeden tanımsız sayılır.
// RefreshMinutes dolunca ilk ihtiyaçta yeniden okunur. TransactionType / MtiProcessingCode API'den değişince handler'lar
// Invalidate ile hemen boşaltır; EntryMode ve InternalResponseCode'un API'si yok (doğrudan SQL), değişikliği en geç
// RefreshMinutes içinde devreye girer.
public sealed class ParameterCache(
    IParameterRepository repository, IMemoryCache cache, TimeProvider clock, IOptions<ParameterOptions> options,
    ILogger<ParameterCache> logger)
{
    private const string Key = "parameters";

    // Aynı anda süresi dolan cache'i birden fazla işlem birlikte yüklemesin.
    private readonly SemaphoreSlim _loading = new(1, 1);

    // Invalidate her çağrıldığında artar: yükleme sürerken gelen bir Invalidate, o yüklemenin (eski olabilecek) sonucunu cache'e yazdırmaz.
    private long _version;

    private sealed record Snapshot(
        IReadOnlyDictionary<(string Mti, string ProcessingCode), RoutedType> Routes,
        IReadOnlyDictionary<string, EntryMode> EntryModes,
        IReadOnlyDictionary<string, string> ResponseCodes,
        DateTimeOffset LoadedAt);

    public async Task<RoutedType?> FindTypeAsync(string mti, string processingCode) =>
        (await GetAsync()).Routes.GetValueOrDefault((mti, processingCode));

    public async Task<EntryMode?> FindEntryModeAsync(string code) =>
        (await GetAsync()).EntryModes.GetValueOrDefault(code);

    // IRC'nin F39 karşılığı; null: IRC tabloda tanımlı değil.
    public async Task<string?> FindResponseCodeAsync(string irc) =>
        (await GetAsync()).ResponseCodes.GetValueOrDefault(irc);

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        cache.Remove(Key);
        logger.LogInformation("{Method}: parameter cache cleared, next transaction reloads definitions", nameof(Invalidate));
    }

    private async Task<Snapshot> GetAsync()
    {
        if (TryGetFresh(out var snapshot))
            return snapshot;

        await _loading.WaitAsync();
        try
        {
            if (TryGetFresh(out snapshot))
                return snapshot;

            var version = Interlocked.Read(ref _version);
            var routes = await repository.GetRoutesAsync();
            var entryModes = await repository.GetEntryModesAsync();
            var responseCodes = await repository.GetInternalResponseCodesAsync();
            snapshot = new Snapshot(
                routes.ToDictionary(r => (r.Mti, r.ProcessingCode), r => new RoutedType(r.Otc, r.Ots, r.Name)),
                entryModes.ToDictionary(e => e.Code),
                responseCodes.ToDictionary(r => r.Irc, r => r.ResponseCode),
                clock.GetUtcNow());

            if (Interlocked.Read(ref _version) == version)
                cache.Set(Key, snapshot);
            logger.LogInformation(
                "{Method}: loaded {Routes} transaction routes, {EntryModes} entry modes and {ResponseCodes} response codes, next refresh in {Minutes} min",
                nameof(GetAsync), snapshot.Routes.Count, snapshot.EntryModes.Count, snapshot.ResponseCodes.Count, options.Value.RefreshMinutes);
            return snapshot;
        }
        finally
        {
            _loading.Release();
        }
    }

    private bool TryGetFresh(out Snapshot snapshot) =>
        cache.TryGetValue(Key, out snapshot!)
        && clock.GetUtcNow() - snapshot.LoadedAt < TimeSpan.FromMinutes(options.Value.RefreshMinutes);
}
