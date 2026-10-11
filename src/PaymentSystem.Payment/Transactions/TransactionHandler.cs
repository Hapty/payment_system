using System.Security.Cryptography;
using System.Text;
using PaymentSystem.Iso8583;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Parameters;

namespace PaymentSystem.Payment.Transactions;

// Gate'ten gelen bir finansal isteği karara bağlar. Kararlar burada, SQL TransactionRepository'de.
// Karar bir iç koddur (IRC, bkz. InternalResponseCodes); POS'a dönen F39 InternalResponseCode tablosundan (ParameterCache) okunur.
// İşlem kaydı, bakiye ve kart güncellemesi tek bir SQL transaction'ında (IDebitSession) yapılır: ya hepsi yazılır ya hiçbiri.
// Onayda F39'u döner; onaylanmayan her işlem (red ya da teknik hata) TransactionException fırlatır.
public sealed class TransactionHandler(
    ITransactionRepository repository, ParameterCache parameters, ExchangeRateCache rates, TimeProvider clock,
    ILogger<TransactionHandler> logger)
{
    private const string TurkishLira = "949";
    private const string ActiveAccount = "Active";

    // UUID v5 için bu sisteme ait sabit namespace. Değiştirilirse eski kayıtların anahtarı yeniden hesaplanamaz.
    private static readonly Guid IdempotencyNamespace = new("6f1c2a7e-3b8d-4c5f-9a0e-2d7b8c1e4f35");

    // İşlem ilerledikçe öğrenilen ve hata anında exception'a konan bilgiler.
    private sealed class Trace
    {
        public Guid? Id { get; set; }
        public string? AccountNo { get; set; }
    }

    // Onay: F39. Red ya da teknik hata: TransactionException (F39, açıklama, maskeli kart no, işlem Guid'i, hesap no).
    public async Task<string> ProcessAsync(TransactionRequest request)
    {
        var trace = new Trace();
        try
        {
            return await AuthorizeAsync(request, trace);
        }
        catch (Exception ex) when (ex is not TransactionException)
        {
            // Örn. DB'ye ulaşılamıyor. Transaction commit edilmediği için para hareketi yok; Gate'e "şu an yapılamıyor" denir.
            throw new TransactionException(
                ResponseCodes.SystemUnavailable, irc: null, "System unavailable", request.CardNumber, trace.Id, trace.AccountNo, ex);
        }
    }

    private async Task<string> AuthorizeAsync(TransactionRequest request, Trace trace)
    {
        // MTI + F3 -> (Otc, Ots) -> işlem adı. Tanımsız bir işlem için kayıt açılmaz: para hareketi yok, tekrarı da aynı cevabı alır.
        var type = await parameters.FindTypeAsync(request.Mti, request.ProcessingCode);
        if (type is null)
        {
            logger.LogWarning("{Method}: no transaction type for MTI {Mti} F3 {ProcessingCode} (TID {TerminalId} STAN {Stan}) - IRC {Irc}",
                nameof(AuthorizeAsync), request.Mti, request.ProcessingCode, request.TerminalId, request.Stan,
                InternalResponseCodes.TransactionTypeNotDefined);
            throw await FailureAsync(request, InternalResponseCodes.TransactionTypeNotDefined, trace);
        }

        // TL karşılığı bir kez, kayıt açılmadan hesaplanır: aynı kur hem LocalTxnAmount'a yazılır hem bakiyeden düşülür.
        var lira = ToLira(request);
        logger.LogInformation("{Method}: {Type} TID {TerminalId} STAN {Stan} PAN {Pan} amount {Amount} {Currency} (TRY {Lira})",
            nameof(AuthorizeAsync), type.Name, request.TerminalId, request.Stan, PanMask.Mask(request.CardNumber),
            ToAmount(request), request.CurrencyCode, lira);

        // Kaydın Guid'i isteğin kendisinden türetilir (bkz. IdempotencyKey): tekrar gelen istek aynı Guid'i üretir ve PK'ya takılır.
        // Aynı anda gelirse ilk transaction bitene kadar bekler, sonra hata alır; böylece aynı işlem asla iki kez karara bağlanmaz.
        var id = IdempotencyKey(request);
        trace.Id = id;
        string? irc = null;
        InternalResponseCode? definition = null;
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
                irc = await DecideAsync(session, request, type.Name, lira, trace);
                definition = await parameters.FindInternalResponseCodeAsync(irc);
                await session.SetResponseCodeAsync(id, definition?.ResponseCode ?? ResponseCodes.SystemMalfunction);
                await session.CommitAsync();
            }
        }

        // Tekrar gelen istek: oturum commit edilmeden kapandı (rollback), önceki karar ayrı bir bağlantıyla okunur.
        if (irc is null)
            return await PreviousResponseAsync(request, id, trace);

        logger.LogInformation("{Method}: {Type} {Id} TID {TerminalId} STAN {Stan} PAN {Pan} committed -> IRC {Irc} F39 {ResponseCode}",
            nameof(AuthorizeAsync), type.Name, id, request.TerminalId, request.Stan, PanMask.Mask(request.CardNumber), irc,
            definition?.ResponseCode ?? ResponseCodes.SystemMalfunction);

        // Red kaydı commit edildi; exception ancak şimdi fırlatılır ki kayıt geri alınmasın.
        if (irc == InternalResponseCodes.Approved && definition is not null)
            return definition.ResponseCode;
        throw Failure(request, irc, definition, trace);
    }

    private async Task<string> DecideAsync(IDebitSession session, TransactionRequest request, string typeName, decimal? lira, Trace trace)
    {
        // F22 opsiyonel; geldiyse ilk iki hanesi EntryMode tablosunda tanımlı olmalı (3. hane PIN yeteneği, karara girmez).
        EntryMode? entryMode = null;
        if (request.EntryMode is { } f22)
        {
            entryMode = f22.Length == 3 ? await parameters.FindEntryModeAsync(f22[..2]) : null;
            if (entryMode is null)
                return Decline(nameof(DecideAsync), request, InternalResponseCodes.EntryModeNotDefined, $"F22 entry mode {f22} is not defined");
        }

        var card = await session.GetCardAsync(request.CardNumber);
        if (card is null)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.CardNotFound, "card not found");
        trace.AccountNo = card.AccountNo;

        if (IsExpired(card.ExpiryDate) || !ExpiryMatches(card.ExpiryDate, request.CardExpiry))
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.CardExpired, "card expired or F14 does not match");

        var accountStatus = await session.GetAccountStatusAsync(card.AccountNo);
        if (accountStatus != ActiveAccount)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.AccountNotActive, $"account {card.AccountNo} is {accountStatus ?? "missing"}");

        return typeName switch
        {
            "BalanceInquiry" => InternalResponseCodes.Approved,     // bakiye cevaba (F54) sonraki adımda eklenecek
            "Sale" => await SaleAsync(session, request, card, lira, entryMode),
            // MoneyTransfer: alıcı kart numarası henüz ISO mesajında / Gate sözleşmesinde yok.
            _ => Decline(nameof(DecideAsync), request, InternalResponseCodes.TransactionNotSupported, $"{typeName} is not supported")
        };
    }

    // Limit, bakiye ve kartın son işlem tutarı hep TL karşılık (lira) üzerinden yürür; hesaplar TL'dir.
    private async Task<string> SaleAsync(IDebitSession session, TransactionRequest request, Card card, decimal? lira, EntryMode? entryMode)
    {
        // Kur yok: ya bu para birimi için TCMB kuru hiç yok (1004) ya da cache'te kur yok / bayat (9001, sonra tekrar denenebilir).
        if (lira is not { } amount)
            return ExchangeRateCache.IsSupported(request.CurrencyCode)
                ? Decline(nameof(SaleAsync), request, InternalResponseCodes.ExchangeRateUnavailable, $"no fresh rate for currency {request.CurrencyCode}")
                : Decline(nameof(SaleAsync), request, InternalResponseCodes.CurrencyNotSupported, $"currency {request.CurrencyCode} is not supported");

        if (amount <= 0)
            return Decline(nameof(SaleAsync), request, InternalResponseCodes.ZeroAmount, "amount is zero");

        // Temassız olup olmadığı EntryMode.IsContactless'tan gelir; F22 yoksa temassız sayılmaz.
        if (entryMode is { IsContactless: true })
        {
            if (!card.ContactlessAllowed)
                return Decline(nameof(SaleAsync), request, InternalResponseCodes.ContactlessNotAllowed, "contactless not allowed for card");
            if (card.ContactlessLimit is { } limit && amount > limit)
                return Decline(nameof(SaleAsync), request, InternalResponseCodes.ContactlessLimitExceeded, $"TRY {amount} exceeds contactless limit {limit}");
        }

        // Bakiye kontrolü ve düşme tek sorguda: aynı hesaba aynı anda gelen iki satış bakiyeyi eksiye düşüremez.
        if (!await session.TryDebitAsync(card.AccountNo, amount))
            return Decline(nameof(SaleAsync), request, InternalResponseCodes.InsufficientFunds, $"insufficient funds for TRY {amount}");

        await session.SetCardLastTransactionAsync(card.CardNumber, amount);
        logger.LogInformation("{Method}: debited TRY {Amount} from account {AccountNo} STAN {Stan} PAN {Pan}",
            nameof(SaleAsync), amount, card.AccountNo, request.Stan, PanMask.Mask(request.CardNumber));
        return InternalResponseCodes.Approved;
    }

    // Red kararını sebebiyle loglar ve iç kodu (IRC) döner; F39 InternalResponseCode tablosundan bulunur.
    private string Decline(string method, TransactionRequest request, string irc, string reason)
    {
        logger.LogWarning("{Method}: declined STAN {Stan} PAN {Pan} - {Reason} -> IRC {Irc}",
            method, request.Stan, PanMask.Mask(request.CardNumber), reason, irc);
        return irc;
    }

    private async Task<TransactionException> FailureAsync(TransactionRequest request, string irc, Trace trace) =>
        Failure(request, irc, await parameters.FindInternalResponseCodeAsync(irc), trace);

    // IRC tabloda tanımlıysa onun F39'u ve açıklaması; değilse hata loglanır ve 96: işlem asla tahminle onaylanmaz.
    private TransactionException Failure(TransactionRequest request, string irc, InternalResponseCode? definition, Trace trace)
    {
        if (definition is not null)
            return new TransactionException(definition.ResponseCode, irc, definition.Description, request.CardNumber, trace.Id, trace.AccountNo);

        logger.LogError("{Method}: IRC {Irc} is not defined in InternalResponseCode (STAN {Stan} PAN {Pan}) - responding {ResponseCode}",
            nameof(Failure), irc, request.Stan, PanMask.Mask(request.CardNumber), ResponseCodes.SystemMalfunction);
        return new TransactionException(
            ResponseCodes.SystemMalfunction, irc, $"IRC {irc} is not defined", request.CardNumber, trace.Id, trace.AccountNo);
    }

    // Tekrar gelen istek: ilk işlemin kararı aynen döner (ilk istek onaylandıysa tekrarı da onay alır ama para tekrar düşmez).
    // İlk karar redse tekrarı da aynı F39 ile TransactionException alır; kayıtta IRC tutulmadığı için açıklama F39'dan bulunur.
    private async Task<string> PreviousResponseAsync(TransactionRequest request, Guid id, Trace trace)
    {
        var previous = (await repository.GetPreviousAsync(id))!;   // kayıt sadece F39 yazılarak commit edildiği için boş olamaz
        trace.AccountNo = previous.AccountNo;

        logger.LogWarning("{Method}: duplicate request {Id} (TID {TerminalId} STAN {Stan} PAN {Pan}) - returning previous F39 {ResponseCode}",
            nameof(PreviousResponseAsync), id, request.TerminalId, request.Stan, PanMask.Mask(request.CardNumber), previous.ResponseCode);

        var approved = await parameters.FindInternalResponseCodeAsync(InternalResponseCodes.Approved);
        if (previous.ResponseCode == approved?.ResponseCode)
            return previous.ResponseCode;

        var descriptions = await parameters.FindDescriptionsAsync(previous.ResponseCode);
        var description = descriptions.Count > 0 ? string.Join(" / ", descriptions) : $"Declined with F39 {previous.ResponseCode}";
        throw new TransactionException(
            previous.ResponseCode, irc: null, $"Duplicate request: {description}", request.CardNumber, id, previous.AccountNo);
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
