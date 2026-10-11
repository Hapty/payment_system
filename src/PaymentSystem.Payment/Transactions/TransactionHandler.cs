using System.Security.Cryptography;
using System.Text;
using PaymentSystem.Iso8583;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Transactions;

// Gate'ten gelen bir finansal isteği karara bağlar ve F39 cevap kodunu döner. Kararlar burada, SQL TransactionRepository'de.
// İşlem kaydı, bakiye ve kart güncellemesi tek bir SQL transaction'ında (IDebitSession) yapılır: ya hepsi yazılır ya hiçbiri.
public sealed class TransactionHandler(
    ITransactionRepository repository, ExchangeRateCache rates, TimeProvider clock, ILogger<TransactionHandler> logger)
{
    private const string TurkishLira = "949";
    private const string ActiveAccount = "Active";

    // UUID v5 için bu sisteme ait sabit namespace. Değiştirilirse eski kayıtların anahtarı yeniden hesaplanamaz.
    private static readonly Guid IdempotencyNamespace = new("6f1c2a7e-3b8d-4c5f-9a0e-2d7b8c1e4f35");

    public async Task<string> ProcessAsync(TransactionRequest request)
    {
        // MTI + F3 -> (Otc, Ots) -> işlem adı. Tanımsız bir işlem için kayıt açılmaz: para hareketi yok, tekrarı da aynı cevabı alır.
        var type = await repository.FindTypeAsync(request.Mti, request.ProcessingCode);
        if (type is null)
        {
            logger.LogWarning("{Method}: no transaction type for MTI {Mti} F3 {ProcessingCode} (TID {TerminalId} STAN {Stan}) - responding 12",
                nameof(ProcessAsync), request.Mti, request.ProcessingCode, request.TerminalId, request.Stan);
            return ResponseCodes.InvalidTransaction;
        }

        // TL karşılığı bir kez, kayıt açılmadan hesaplanır: aynı kur hem LocalTxnAmount'a yazılır hem bakiyeden düşülür.
        var lira = ToLira(request);
        logger.LogInformation("{Method}: {Type} TID {TerminalId} STAN {Stan} PAN {Pan} amount {Amount} {Currency} (TRY {Lira})",
            nameof(ProcessAsync), type.Name, request.TerminalId, request.Stan, PanMask.Mask(request.CardNumber),
            ToAmount(request), request.CurrencyCode, lira);

        // Kaydın Guid'i isteğin kendisinden türetilir (bkz. IdempotencyKey): tekrar gelen istek aynı Guid'i üretir ve PK'ya takılır.
        // Aynı anda gelirse ilk transaction bitene kadar bekler, sonra hata alır; böylece aynı işlem asla iki kez karara bağlanmaz.
        var id = IdempotencyKey(request);
        string? responseCode = null;
        await using (var session = await repository.BeginAsync())
        {
            var inserted = await session.TryInsertAsync(new DebitTransaction(
                id, request.Mti, request.CardNumber, request.ProcessingCode, type.Otc, type.Ots,
                Amount: ToAmount(request),
                request.CurrencyCode,
                LocalAmount: lira,
                request.LocalTime,
                TransactionDate: ToTransactionDate(request.LocalDate),
                request.CardExpiry, request.MerchantType, request.EntryMode, CardAcceptor: request.CardAcceptor?.TrimEnd()));
            if (inserted)
            {
                responseCode = await DecideAsync(session, request, type.Name, lira);
                await session.SetResponseCodeAsync(id, responseCode);
                await session.CommitAsync();
            }
        }

        // Tekrar gelen istek: oturum commit edilmeden kapandı (rollback), önceki karar ayrı bir bağlantıyla okunur.
        if (responseCode is null)
            return await PreviousResponseAsync(request, id);

        logger.LogInformation("{Method}: {Type} {Id} TID {TerminalId} STAN {Stan} PAN {Pan} committed -> F39 {ResponseCode}",
            nameof(ProcessAsync), type.Name, id, request.TerminalId, request.Stan, PanMask.Mask(request.CardNumber), responseCode);
        return responseCode;
    }

    private async Task<string> DecideAsync(IDebitSession session, TransactionRequest request, string typeName, decimal? lira)
    {
        // F22 opsiyonel; geldiyse ilk iki hanesi EntryMode tablosunda tanımlı olmalı (3. hane PIN yeteneği, karara girmez).
        EntryMode? entryMode = null;
        if (request.EntryMode is { } f22)
        {
            entryMode = f22.Length == 3 ? await session.GetEntryModeAsync(f22[..2]) : null;
            if (entryMode is null)
                return Decline(nameof(DecideAsync), request, ResponseCodes.InvalidTransaction, $"F22 entry mode {f22} is not defined");
        }

        var card = await session.GetCardAsync(request.CardNumber);
        if (card is null)
            return Decline(nameof(DecideAsync), request, ResponseCodes.InvalidCardNumber, "card not found");

        if (IsExpired(card.ExpiryDate) || !ExpiryMatches(card.ExpiryDate, request.CardExpiry))
            return Decline(nameof(DecideAsync), request, ResponseCodes.ExpiredCard, "card expired or F14 does not match");

        var accountStatus = await session.GetAccountStatusAsync(card.AccountNo);
        if (accountStatus != ActiveAccount)
            return Decline(nameof(DecideAsync), request, ResponseCodes.RestrictedCard, $"account {card.AccountNo} is {accountStatus ?? "missing"}");

        return typeName switch
        {
            "BalanceInquiry" => ResponseCodes.Approved,     // bakiye cevaba (F54) sonraki adımda eklenecek
            "Sale" => await SaleAsync(session, request, card, lira, entryMode),
            // MoneyTransfer: alıcı kart numarası henüz ISO mesajında / Gate sözleşmesinde yok.
            _ => Decline(nameof(DecideAsync), request, ResponseCodes.InvalidTransaction, $"{typeName} is not supported")
        };
    }

    // Limit, bakiye ve kartın son işlem tutarı hep TL karşılık (lira) üzerinden yürür; hesaplar TL'dir.
    private async Task<string> SaleAsync(IDebitSession session, TransactionRequest request, Card card, decimal? lira, EntryMode? entryMode)
    {
        // Kur yok: ya bu para birimi için TCMB kuru hiç yok (12) ya da cache'te kur yok / bayat (91, sonra tekrar denenebilir).
        if (lira is not { } amount)
            return ExchangeRateCache.IsSupported(request.CurrencyCode)
                ? Decline(nameof(SaleAsync), request, ResponseCodes.SystemUnavailable, $"no fresh rate for currency {request.CurrencyCode}")
                : Decline(nameof(SaleAsync), request, ResponseCodes.InvalidTransaction, $"currency {request.CurrencyCode} is not supported");

        if (amount <= 0)
            return Decline(nameof(SaleAsync), request, ResponseCodes.InvalidAmount, "amount is zero");

        // Temassız olup olmadığı EntryMode.IsContactless'tan gelir; F22 yoksa temassız sayılmaz.
        if (entryMode is { IsContactless: true })
        {
            if (!card.ContactlessAllowed)
                return Decline(nameof(SaleAsync), request, ResponseCodes.NotPermittedToCardholder, "contactless not allowed for card");
            if (card.ContactlessLimit is { } limit && amount > limit)
                return Decline(nameof(SaleAsync), request, ResponseCodes.ContactlessLimitExceeded, $"TRY {amount} exceeds contactless limit {limit}");
        }

        // Bakiye kontrolü ve düşme tek sorguda: aynı hesaba aynı anda gelen iki satış bakiyeyi eksiye düşüremez.
        if (!await session.TryDebitAsync(card.AccountNo, amount))
            return Decline(nameof(SaleAsync), request, ResponseCodes.InsufficientFunds, $"insufficient funds for TRY {amount}");

        await session.SetCardLastTransactionAsync(card.CardNumber, amount);
        logger.LogInformation("{Method}: debited TRY {Amount} from account {AccountNo} STAN {Stan} PAN {Pan}",
            nameof(SaleAsync), amount, card.AccountNo, request.Stan, PanMask.Mask(request.CardNumber));
        return ResponseCodes.Approved;
    }

    // Red kararını sebebiyle loglar ve cevap kodunu döner.
    private string Decline(string method, TransactionRequest request, string responseCode, string reason)
    {
        logger.LogWarning("{Method}: declined STAN {Stan} PAN {Pan} - {Reason} -> F39 {ResponseCode}",
            method, request.Stan, PanMask.Mask(request.CardNumber), reason, responseCode);
        return responseCode;
    }

    // Tekrar gelen istek: ilk işlemin kararı aynen döner (ilk istek onaylandıysa tekrarı da "00" alır ama para tekrar düşmez).
    private async Task<string> PreviousResponseAsync(TransactionRequest request, Guid id)
    {
        var previous = await repository.GetResponseCodeAsync(id);

        logger.LogWarning("{Method}: duplicate request {Id} (TID {TerminalId} STAN {Stan} PAN {Pan}) - returning previous F39 {ResponseCode}",
            nameof(PreviousResponseAsync), id, request.TerminalId, request.Stan, PanMask.Mask(request.CardNumber), previous);
        return previous!;   // kayıt sadece F39 yazılarak commit edildiği için boş olamaz
    }

    // Tabloda STAN / terminal no olmadığı için istek, tekrarında da aynı kalan alanlarla tanımlanır: MTI, kart, işlem kodu,
    // tutar, POS'un yerel tarih ve saati (saniye). Bunlardan deterministik bir UUID v5 (RFC 4122, SHA-1) üretilir.
    // Bilinen sınır: aynı kartla aynı saniyede aynı tutarda iki gerçek işlem, ikincisi tekrar sayılır.
    private static Guid IdempotencyKey(TransactionRequest request)
    {
        var name = string.Join('|', request.Mti, request.CardNumber, request.ProcessingCode, request.AmountMinor,
            request.LocalDate, request.LocalTime);

        var input = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        IdempotencyNamespace.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));

        var hash = SHA1.HashData(input).AsSpan(0, 16).ToArray();
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);     // versiyon 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);     // RFC 4122 varyantı
        return new Guid(hash, bigEndian: true);
    }

    private static decimal ToAmount(TransactionRequest request) => request.AmountMinor / 100m;

    // TL: tutarın kendisi. Döviz: cache'teki TCMB döviz satış kuruyla TL karşılığı, kuruşa yuvarlanmış. Kur yoksa null.
    private decimal? ToLira(TransactionRequest request)
    {
        if (request.CurrencyCode == TurkishLira)
            return ToAmount(request);
        if (!rates.TryGetSellingRate(request.CurrencyCode, out var rate))
            return null;
        return decimal.Round(ToAmount(request) * rate, 2, MidpointRounding.AwayFromZero);
    }

    // Card.ExpiryDate MMYY: kart o ayın sonuna kadar geçerlidir.
    private bool IsExpired(string? expiryDate)
    {
        if (expiryDate is null)
            return false;
        var today = clock.GetUtcNow();
        var (month, year) = (int.Parse(expiryDate[..2]), 2000 + int.Parse(expiryDate[2..]));
        return (year, month).CompareTo((today.Year, today.Month)) < 0;
    }

    // POS F14'ü YYMM, Card tablosu MMYY formatında tutar.
    private static bool ExpiryMatches(string? cardExpiry, string? requestExpiry) =>
        cardExpiry is null || requestExpiry is null || requestExpiry == cardExpiry[2..] + cardExpiry[..2];

    // F13 sadece MMDD taşır, kolon yyyymmdd ister. Yıl bugünün yılıdır; yılbaşı civarında POS'un tarihi hâlâ
    // geçen yılın Aralık'ıysa (bugünden 1 aydan fazla ileride görünen ay) bir önceki yıl alınır.
    private string ToTransactionDate(string mmdd)
    {
        var today = clock.GetUtcNow();
        var year = int.Parse(mmdd[..2]) > today.Month + 1 ? today.Year - 1 : today.Year;
        return $"{year:D4}{mmdd}";
    }
}
