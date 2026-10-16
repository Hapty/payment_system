using System.Security.Cryptography;
using System.Text;
using PaymentSystem.Iso8583;
using PaymentSystem.Payment.Cards;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Events;
using PaymentSystem.Payment.ExchangeRates;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Parameters;

namespace PaymentSystem.Payment.Transactions;

// Gate'ten gelen bir finansal isteği karara bağlar. Kararlar burada, SQL TransactionRepository'de.
// Karar bir iç koddur (IRC, bkz. InternalResponseCodes); POS'a dönen F39 InternalResponseCode tablosundan (ParameterCache) okunur.
// İşlem kaydı, bakiye ve kart güncellemesi tek bir SQL transaction'ında (IDebitSession) yapılır: ya hepsi yazılır ya hiçbiri.
// Commit edilen her karar (onay da red de) TransactionCompleted olayı olarak yayımlanır; yayımlama işlemi bekletmez.
// Onayda F39'u döner; onaylanmayan her işlem (red ya da teknik hata) TransactionException fırlatır.
public sealed class TransactionHandler(
    ITransactionRepository repository, ParameterCache parameters, ExchangeRateCache rates, ICardSecretHasher secrets,
    ITransactionEventPublisher events, TimeProvider clock, ILogger<TransactionHandler> logger)
{
    private const string TurkishLira = "949";
    private const string ActiveAccount = "Active";
    private const string MoneyTransfer = "MoneyTransfer";

    // Kart fiziksel olarak okutulmadığında CVV2 zorunludur: elle giriş (F22 01x) ve e-ticaret (81x).
    // Çip / temassız işlemde kartın kendisi kriptogram üretir, CVV2 istenmez.
    private static readonly HashSet<string> CardNotPresentEntryModes = ["01", "81"];

    // UUID v5 için bu sisteme ait sabit namespace. Değiştirilirse eski kayıtların anahtarı yeniden hesaplanamaz.
    private static readonly Guid IdempotencyNamespace = new("6f1c2a7e-3b8d-4c5f-9a0e-2d7b8c1e4f35");

    // İşlem ilerledikçe öğrenilen ve hata anında exception'a konan bilgiler.
    private sealed class Trace
    {
        public Guid? Id { get; set; }
        public string? AccountNo { get; set; }
        public string? Email { get; set; }      // sadece olaya konur, exception'a değil
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
        var transactionDate = request.LocalDate;     // yyyymmdd: Gate yılıyla birlikte gönderir
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
                TransactionDate: transactionDate,
                request.CardExpiry, request.MerchantType, request.EntryMode, CardAcceptor: request.CardAcceptor?.TrimEnd(),
                // Kolon VARCHAR(19): kart numarası olmayan F48 yazılmaz, transfer 1005 ile reddedilir.
                RecipientCardNumber: type.Name == MoneyTransfer && IsCardNumber(request.RecipientCardNumber) ? request.RecipientCardNumber : null));
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

        var responseCode = definition?.ResponseCode ?? ResponseCodes.SystemMalfunction;
        logger.LogInformation("{Method}: {Type} {Id} TID {TerminalId} STAN {Stan} PAN {Pan} committed -> IRC {Irc} F39 {ResponseCode}",
            nameof(AuthorizeAsync), type.Name, id, request.TerminalId, request.Stan, PanMask.Mask(request.CardNumber), irc,
            responseCode);

        // Sadece commit edilen karar yayımlanır: tekrar gelen istek ve kayıt açılmayan red (tanımsız tip) olay üretmez.
        events.Publish(new TransactionCompleted(
            id, clock.GetUtcNow(), request.Mti, type.Name, responseCode, irc, PanMask.Mask(request.CardNumber),
            trace.AccountNo, trace.Email, ToAmount(request), request.CurrencyCode, lira, request.CardAcceptor?.TrimEnd(),
            transactionDate, request.LocalTime, request.TerminalId, request.Stan));

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

        // CVV2 kart fiziksel olarak yokken zorunlu; başka bir girişte de geldiyse kontrol edilir. Değeri asla loglanmaz.
        if ((entryMode is not null && CardNotPresentEntryModes.Contains(entryMode.Code)) || request.Cvv is not null)
        {
            if (request.Cvv is null)
                return Decline(nameof(DecideAsync), request, InternalResponseCodes.CvvMismatch, $"CVV2 required for entry mode {request.EntryMode}");
            if (!await CvvMatchesAsync(session, request.CardNumber, request.Cvv))
                return Decline(nameof(DecideAsync), request, InternalResponseCodes.CvvMismatch, "CVV2 does not match");
        }

        var account = await session.GetAccountAsync(card.AccountNo);
        trace.Email = account?.Email;
        if (account?.AccountStatus != ActiveAccount)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.AccountNotActive,
                $"account {card.AccountNo} is {account?.AccountStatus ?? "missing"}");

        return typeName switch
        {
            "BalanceInquiry" => InternalResponseCodes.Approved,     // bakiye cevaba (F54) sonraki adımda eklenecek
            "Sale" => await SaleAsync(session, request, card, lira, entryMode),
            MoneyTransfer => await TransferAsync(session, request, card, lira),
            _ => Decline(nameof(DecideAsync), request, InternalResponseCodes.TransactionNotSupported, $"{typeName} is not supported")
        };
    }

    // Limit, bakiye ve kartın son işlem tutarı hep TL karşılık (lira) üzerinden yürür; hesaplar TL'dir.
    private async Task<string> SaleAsync(IDebitSession session, TransactionRequest request, Card card, decimal? lira, EntryMode? entryMode)
    {
        var (amount, declined) = CheckAmount(nameof(SaleAsync), request, lira);
        if (declined is not null)
            return declined;

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

    // Karttan karta para transferi (sadece bu sistemdeki kartlar arası): F48'deki alıcı kartın hesabına, gönderenin hesabından.
    // Döviz de olabilir; iki hesap da TL olduğu için TL karşılığı düşülür ve eklenir. Temassız limit uygulanmaz,
    // alıcı kartın son kullanma tarihine bakılmaz. Gönderen kartın kontrolleri (kart, F14, CVV2, hesap) DecideAsync'te yapıldı.
    private async Task<string> TransferAsync(IDebitSession session, TransactionRequest request, Card card, decimal? lira)
    {
        if (request.RecipientCardNumber is not { } recipientNumber || !IsCardNumber(recipientNumber))
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.RecipientCardInvalid, "F48 recipient card number missing or invalid");

        var recipient = await session.GetCardAsync(recipientNumber);
        if (recipient is null)
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.RecipientCardNotFound,
                $"recipient card {PanMask.Mask(recipientNumber)} not found");
        if (recipient.AccountNo == card.AccountNo)
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.SameAccountTransfer,
                $"recipient card {PanMask.Mask(recipientNumber)} is on the same account {card.AccountNo}");

        var recipientAccount = await session.GetAccountAsync(recipient.AccountNo);
        if (recipientAccount?.AccountStatus != ActiveAccount)
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.RecipientAccountNotActive,
                $"recipient account {recipient.AccountNo} is {recipientAccount?.AccountStatus ?? "missing"}");

        var (amount, declined) = CheckAmount(nameof(TransferAsync), request, lira);
        if (declined is not null)
            return declined;

        if (!await session.TryTransferAsync(card.AccountNo, recipient.AccountNo, amount))
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.InsufficientFunds, $"insufficient funds for TRY {amount}");

        await session.SetCardLastTransactionAsync(card.CardNumber, amount);
        logger.LogInformation("{Method}: transferred TRY {Amount} from account {FromAccountNo} to account {ToAccountNo} STAN {Stan} PAN {Pan} -> {RecipientPan}",
            nameof(TransferAsync), amount, card.AccountNo, recipient.AccountNo, request.Stan, PanMask.Mask(request.CardNumber),
            PanMask.Mask(recipientNumber));
        return InternalResponseCodes.Approved;
    }

    // Satış ve transferde tutar: kur yoksa ya bu para birimi için TCMB kuru hiç yok (1004) ya da cache'te kur yok / bayat
    // (9001, sonra tekrar denenebilir); TL karşılığı 0 olamaz. Geçerliyse TL karşılığı, değilse red IRC'si döner.
    private (decimal Lira, string? Declined) CheckAmount(string method, TransactionRequest request, decimal? lira)
    {
        if (lira is not { } amount)
            return (0, ExchangeRateCache.IsSupported(request.CurrencyCode)
                ? Decline(method, request, InternalResponseCodes.ExchangeRateUnavailable, $"no fresh rate for currency {request.CurrencyCode}")
                : Decline(method, request, InternalResponseCodes.CurrencyNotSupported, $"currency {request.CurrencyCode} is not supported"));

        if (amount <= 0)
            return (0, Decline(method, request, InternalResponseCodes.ZeroAmount, "amount is zero"));
        return (amount, null);
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

    // CVV düz saklanmaz: gelen değer hash'lenip dbo.CardSecret'taki hash ile karşılaştırılır (sabit zamanlı; süre hangi
    // baytın tuttuğunu ele vermez). CardSecret'ı olmayan kart (bu tablodan önce açılmış) eşleşmemiş sayılır.
    private async Task<bool> CvvMatchesAsync(IDebitSession session, string cardNumber, string received) =>
        await session.GetCvvHashAsync(cardNumber) is { } stored
        && CryptographicOperations.FixedTimeEquals(stored, secrets.HashCvv(cardNumber, received));

    // ISO8583 F2 ile aynı kural: 1-19 rakam.
    private static bool IsCardNumber(string? value) => value is { Length: > 0 and <= 19 } && value.All(char.IsAsciiDigit);

    // POS F14'ü YYMM, Card tablosu MMYY formatında tutar.
    private static bool ExpiryMatches(string? cardExpiry, string? requestExpiry) =>
        cardExpiry is null || requestExpiry is null || requestExpiry == cardExpiry[2..] + cardExpiry[..2];
}
