using System.Net;
using System.Xml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentSystem.Payment.Controllers;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Handlers;

namespace PaymentSystem.Payment.Tests;

// TCMB'ye gitmeden: XML ayrıştırma, cache okuma/bayatlama ve arka plan servisinin davranışı (sahte HTTP handler ile).
public class ExchangeRateTests
{
    // Gerçek today.xml'den kısaltılmış örnek: JPY 100 birim için, XDR'de döviz satış kuru boş.
    private const string Bulletin = """
        <?xml version="1.0" encoding="UTF-8"?>
        <?xml-stylesheet type="text/xsl" href="isokur.xsl"?>
        <Tarih_Date Tarih="08.10.2026" Date="10/08/2026" Bulten_No="2026/190">
          <Currency CrossOrder="0" Kod="USD" CurrencyCode="USD">
            <Unit>1</Unit><Isim>ABD DOLARI</Isim><ForexBuying>49.1267</ForexBuying><ForexSelling>49.2152</ForexSelling>
          </Currency>
          <Currency CrossOrder="9" Kod="EUR" CurrencyCode="EUR">
            <Unit>1</Unit><Isim>EURO</Isim><ForexBuying>54.9548</ForexBuying><ForexSelling>55.0538</ForexSelling>
          </Currency>
          <Currency CrossOrder="11" Kod="JPY" CurrencyCode="JPY">
            <Unit>100</Unit><Isim>JAPON YENİ</Isim><ForexBuying>32.1000</ForexBuying><ForexSelling>32.3500</ForexSelling>
          </Currency>
          <Currency CrossOrder="21" Kod="XDR" CurrencyCode="XDR">
            <Unit>1</Unit><Isim>ÖZEL ÇEKME HAKKI (SDR)</Isim><ForexBuying>66.5</ForexBuying><ForexSelling/>
          </Currency>
        </Tarih_Date>
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return respond(request);
        }
    }

    private static Task<HttpResponseMessage> Xml(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    private static ExchangeRatesController Controller(ExchangeRateCache cache) =>
        new(new ExchangeRateHandler(cache, NullLogger<ExchangeRateHandler>.Instance));

    private static ExchangeRateCache NewCache(TimeProvider clock, int maxAgeHours = 24) =>
        new(new MemoryCache(new MemoryCacheOptions()), clock, Options.Create(new ExchangeRateOptions { MaxAgeHours = maxAgeHours }));

    private static (ExchangeRateRefresher Refresher, ExchangeRateCache Cache, StubHttpHandler Handler) NewRefresher(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, ExchangeRateOptions? options = null)
    {
        var clock = new FixedClock(Now);
        var opts = Options.Create(options ?? new ExchangeRateOptions());
        var handler = new StubHttpHandler(respond);
        var cache = NewCache(clock);
        var refresher = new ExchangeRateRefresher(new TcmbClient(new HttpClient(handler), opts), cache, opts, clock,
            NullLogger<ExchangeRateRefresher>.Instance);
        return (refresher, cache, handler);
    }

    [Fact]
    public void Parse_ReadsDateAndForexSellingPerUnit()
    {
        var bulletin = TcmbClient.Parse(Bulletin);

        Assert.Equal(new DateOnly(2026, 10, 8), bulletin.Date);
        Assert.Equal(49.2152m, bulletin.SellingRates["USD"]);
        Assert.Equal(55.0538m, bulletin.SellingRates["EUR"]);
        Assert.Equal(0.3235m, bulletin.SellingRates["JPY"]);     // 100 JPY = 32,35 TL
        Assert.False(bulletin.SellingRates.ContainsKey("XDR"));  // satış kuru boş
    }

    [Fact]
    public void Parse_MalformedXml_Throws() =>
        Assert.ThrowsAny<XmlException>(() => TcmbClient.Parse("<Tarih_Date Tarih=\"08.10.2026\"><Currency"));

    [Fact]
    public void Parse_WithoutDate_Throws() =>
        Assert.Throws<FormatException>(() => TcmbClient.Parse("<Tarih_Date><Currency Kod=\"USD\"><Unit>1</Unit><ForexSelling>1</ForexSelling></Currency></Tarih_Date>"));

    [Fact]
    public void Parse_WithoutAnyRate_Throws() =>
        Assert.Throws<FormatException>(() => TcmbClient.Parse("<Tarih_Date Tarih=\"08.10.2026\"></Tarih_Date>"));

    [Fact]
    public void Cache_ReturnsStoredRateByIsoNumericCode()
    {
        var cache = NewCache(new FixedClock(Now));
        cache.Store(TcmbClient.Parse(Bulletin));

        Assert.True(cache.TryGetSellingRate("840", out var usd));
        Assert.Equal(49.2152m, usd);
        Assert.True(cache.TryGetSellingRate("392", out var jpy));
        Assert.Equal(0.3235m, jpy);
    }

    [Fact]
    public void Cache_UnknownOrNotYetFetchedCurrency_HasNoRate()
    {
        var cache = NewCache(new FixedClock(Now));
        cache.Store(TcmbClient.Parse(Bulletin));

        Assert.False(cache.TryGetSellingRate("999", out _));    // TCMB'de olmayan para birimi
        Assert.False(cache.TryGetSellingRate("826", out _));    // GBP desteklenir ama bu bültende yok
        Assert.True(ExchangeRateCache.IsSupported("826"));
        Assert.False(ExchangeRateCache.IsSupported("999"));
    }

    [Fact]
    public void Cache_RateOlderThanMaxAge_IsStale()
    {
        var clock = new FixedClock(Now);
        var cache = NewCache(clock, maxAgeHours: 24);
        cache.Store(TcmbClient.Parse(Bulletin));

        clock.Now = Now.AddHours(24);
        Assert.True(cache.TryGetSellingRate("840", out _));
        clock.Now = Now.AddHours(24).AddSeconds(1);
        Assert.False(cache.TryGetSellingRate("840", out _));
    }

    [Fact]
    public void Controller_GetAll_ReturnsCachedRatesSortedByCurrency()
    {
        var cache = NewCache(new FixedClock(Now));
        cache.Store(TcmbClient.Parse(Bulletin));

        var all = Controller(cache).GetAll();

        Assert.Equal(["EUR", "JPY", "USD"], all.Select(r => r.Currency));
        Assert.Equal(new CachedRate("840", "USD", 49.2152m, new DateOnly(2026, 10, 8), Now, IsStale: false), all[2]);
    }

    [Fact]
    public void Controller_GetAll_WithEmptyCache_ReturnsEmptyList() =>
        Assert.Empty(Controller(NewCache(new FixedClock(Now))).GetAll());

    [Theory]
    [InlineData("978")]
    [InlineData("EUR")]
    [InlineData("eur")]
    public void Controller_Get_FindsRateByNumericOrAlphaCode(string code)
    {
        var cache = NewCache(new FixedClock(Now));
        cache.Store(TcmbClient.Parse(Bulletin));

        var rate = Controller(cache).Get(code).Value!;

        Assert.Equal(("978", "EUR", 55.0538m), (rate.CurrencyCode, rate.Currency, rate.SellingRate));
    }

    [Fact]
    public void Controller_Get_ShowsStaleRateAsStale()
    {
        var clock = new FixedClock(Now);
        var cache = NewCache(clock);
        cache.Store(TcmbClient.Parse(Bulletin));
        clock.Now = Now.AddHours(25);

        Assert.True(Controller(cache).Get("USD").Value!.IsStale);
    }

    [Theory]
    [InlineData("999")]     // TCMB'de olmayan para birimi
    [InlineData("XDR")]     // TCMB'de var ama döviz satış kuru yok
    [InlineData("GBP")]     // desteklenir ama bu bültende yok
    public void Controller_Get_UnknownOrNotCachedCurrency_ReturnsNotFound(string code)
    {
        var cache = NewCache(new FixedClock(Now));
        cache.Store(TcmbClient.Parse(Bulletin));

        Assert.IsType<NotFoundResult>(Controller(cache).Get(code).Result);
    }

    [Fact]
    public async Task Refresh_FetchesConfiguredUrlAndFillsCache()
    {
        var (refresher, cache, handler) = NewRefresher(_ => Xml(Bulletin), new ExchangeRateOptions { TcmbUrl = "https://tcmb.test/kurlar/today.xml" });

        Assert.True(await refresher.RefreshAsync(CancellationToken.None));

        Assert.Equal("https://tcmb.test/kurlar/today.xml", Assert.Single(handler.Requested).ToString());
        Assert.True(cache.TryGetSellingRate("978", out var eur));
        Assert.Equal(55.0538m, eur);
    }

    [Fact]
    public async Task Refresh_WhenTcmbFails_KeepsPreviousRates()
    {
        var fail = false;
        var (refresher, cache, _) = NewRefresher(_ => fail
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            : Xml(Bulletin));
        await refresher.RefreshAsync(CancellationToken.None);

        fail = true;
        Assert.False(await refresher.RefreshAsync(CancellationToken.None));

        Assert.True(cache.TryGetSellingRate("840", out var usd));
        Assert.Equal(49.2152m, usd);
    }

    [Theory]
    [InlineData("<html>bakım çalışması</html>")]     // XML ama kur bülteni değil
    [InlineData("<Tarih_Date")]                      // bozuk XML
    public async Task Refresh_WithInvalidBulletin_ReturnsFalse(string body)
    {
        var (refresher, cache, _) = NewRefresher(_ => Xml(body));

        Assert.False(await refresher.RefreshAsync(CancellationToken.None));
        Assert.False(cache.TryGetSellingRate("840", out _));
    }

    [Fact]
    public async Task Refresh_WhenTcmbTimesOut_ReturnsFalse()
    {
        var (refresher, _, _) = NewRefresher(_ => throw new TaskCanceledException("timeout"));

        Assert.False(await refresher.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BackgroundService_FetchesOnStartupAndStopsCleanly()
    {
        var fetched = new TaskCompletionSource();
        var (refresher, cache, _) = NewRefresher(_ =>
        {
            fetched.TrySetResult();
            return Xml(Bulletin);
        });

        await refresher.StartAsync(CancellationToken.None);
        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Cevabın işlenip cache'e yazılması, isteğin gönderilmesinden biraz sonra olur.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!cache.TryGetSellingRate("840", out _) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await refresher.StopAsync(CancellationToken.None);

        Assert.True(cache.TryGetSellingRate("840", out _));
    }

    [Fact]
    public async Task BackgroundService_RetriesAfterFailure()
    {
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource();
        var (refresher, _, _) = NewRefresher(async _ =>
        {
            await Task.Yield();
            if (Interlocked.Increment(ref attempts) >= 2)
                secondAttempt.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }, new ExchangeRateOptions { RetryMinutes = 0 });

        await refresher.StartAsync(CancellationToken.None);
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await refresher.StopAsync(CancellationToken.None);
    }
}
