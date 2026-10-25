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
// İşlem kaydı, bakiye / kredi limiti ve kart güncellemesi tek bir SQL transaction'ında (ITransactionSession) yapılır: ya hepsi yazılır ya hiçbiri.
// Kartın tipi Credit ise işlem CreditTransaction'a yazılır ve satış banka hesabından değil kredi limitinden düşer; diğer kartlar debit'tir.
// Commit edilen her karar (onay da red de) TransactionCompleted olayı olarak yayımlanır; yayımlama işlemi bekletmez.
// Onayda F39'u döner; onaylanmayan her işlem (red ya da teknik hata) TransactionException fırlatır.
public sealed class TransactionHandler(
    ITransactionRepository repository, ParameterCache parameters, ExchangeRateCache rates, ICardSecretHasher secrets,
    ITransactionEventPublisher events, TimeProvider clock, ILogger<TransactionHandler> logger)
{
    private const string TurkishLira = "949";
    private const string ActiveAccount = "Active";
    private const string ActiveMerchant = "Active";
    private const string Sale = "Sale";
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
        public long? AccountNo { get; set; }
        public string? Email { get; set; }      // sadece olaya konur, exception'a değil

        // Transferde alıcı; sadece olaya konur (Notification alıcıya da e-posta atar).
        public string? RecipientPan { get; set; }
        public long? RecipientAccountNo { get; set; }
        public string? RecipientEmail { get; set; }
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
            logger.LogWarning("{Method}: no transaction type for MTI {Mti} {ProcessingCode} ({TerminalId} {Stan}) - IRC {Irc}",
                nameof(AuthorizeAsync), request.Mti, IsoLog.Field(3, request.ProcessingCode), IsoLog.Field(41, request.TerminalId),
                IsoLog.Field(11, request.Stan), InternalResponseCodes.TransactionTypeNotDefined);
            throw await FailureAsync(request, InternalResponseCodes.TransactionTypeNotDefined, trace);
        }

        // TL karşılığı bir kez, kayıt açılmadan hesaplanır: aynı kur hem LocalTxnAmount'a yazılır hem bakiyeden düşülür.
        var lira = ToLira(request);
        logger.LogInformation("{Method}: {Type} {TerminalId} {Stan} {Pan} amount {Amount} {Currency} (TRY {Lira})",
            nameof(AuthorizeAsync), type.Name, IsoLog.Field(41, request.TerminalId), IsoLog.Field(11, request.Stan),
            IsoLog.Field(2, request.CardNumber), ToAmount(request), IsoLog.Field(49, request.CurrencyCode), lira);

        // Kaydın Guid'i isteğin kendisinden türetilir (bkz. IdempotencyKey): tekrar gelen istek aynı Guid'i üretir ve PK'ya takılır.
        // Aynı anda gelirse ilk transaction bitene kadar bekler, sonra hata alır; böylece aynı işlem asla iki kez karara bağlanmaz.
        var id = IdempotencyKey(request);
        trace.Id = id;
        var transactionDate = request.LocalDate;     // yyyymmdd: Gate yılıyla birlikte gönderir
        string? irc = null;
        InternalResponseCode? definition = null;
        await using (var session = await repository.BeginAsync())
        {
            // Kaydın hangi tabloya açılacağını kartın tipi belirler; kart yoksa debit tablosuna yazılır (2001 ile reddedilir).
            var card = await session.GetCardAsync(request.CardNumber);
            var inserted = card is { CardType: CardTypes.Credit }
                ? await session.TryInsertAsync(new CreditTransaction(
                    id, request.Mti, request.CardNumber, request.ProcessingCode, type.Otc, type.Ots,
                    Amount: ToAmount(request),
                    request.CurrencyCode,
                    LocalAmount: lira,
                    request.LocalTime,
                    TransactionDate: transactionDate,
                    request.CardExpiry, request.MerchantType, request.EntryMode, MerchantId: request.MerchantId?.TrimEnd(),
                    CardAcceptor: request.CardAcceptor?.TrimEnd(),
                    InstallmentCount: request.InstallmentCount ?? 1))
                : await session.TryInsertAsync(new DebitTransaction(
                    id, request.Mti, request.CardNumber, request.ProcessingCode, type.Otc, type.Ots,
                    Amount: ToAmount(request),
                    request.CurrencyCode,
                    LocalAmount: lira,
                    request.LocalTime,
                    TransactionDate: transactionDate,
                    request.CardExpiry, request.MerchantType, request.EntryMode, MerchantId: request.MerchantId?.TrimEnd(),
                    CardAcceptor: request.CardAcceptor?.TrimEnd(),
                    // Kolon VARCHAR(19): kart numarası olmayan F48 yazılmaz, transfer 1005 ile reddedilir.
                    RecipientCardNumber: type.Name == MoneyTransfer && IsCardNumber(request.RecipientCardNumber) ? request.RecipientCardNumber : null));
            if (inserted)
            {
                irc = await DecideAsync(session, request, type.Name, lira, card, trace);
                definition = await parameters.FindInternalResponseCodeAsync(irc);
                await session.SetResponseCodeAsync(id, definition?.ResponseCode ?? ResponseCodes.SystemMalfunction);
                await session.CommitAsync();
            }
        }

        // Tekrar gelen istek: oturum commit edilmeden kapandı (rollback), önceki karar ayrı bir bağlantıyla okunur.
        if (irc is null)
            return await PreviousResponseAsync(request, id, trace);

        var responseCode = definition?.ResponseCode ?? ResponseCodes.SystemMalfunction;
        logger.LogInformation("{Method}: {Type} {Id} {TerminalId} {Stan} {Pan} committed -> IRC {Irc} {ResponseCode}",
            nameof(AuthorizeAsync), type.Name, id, IsoLog.Field(41, request.TerminalId), IsoLog.Field(11, request.Stan),
            IsoLog.Field(2, request.CardNumber), irc, IsoLog.Field(39, responseCode));

        // Sadece commit edilen karar yayımlanır: tekrar gelen istek ve kayıt açılmayan red (tanımsız tip) olay üretmez.
        events.Publish(new TransactionCompleted(
            id, clock.GetUtcNow(), request.Mti, type.Name, responseCode, irc, PanMask.Mask(request.CardNumber),
            trace.AccountNo, trace.Email, ToAmount(request), request.CurrencyCode, lira, request.CardAcceptor?.TrimEnd(),
            transactionDate, request.LocalTime, request.TerminalId, request.Stan,
            trace.RecipientPan is { } recipientPan ? PanMask.Mask(recipientPan) : null, trace.RecipientAccountNo, trace.RecipientEmail));

        // Red kaydı commit edildi; exception ancak şimdi fırlatılır ki kayıt geri alınmasın.
        if (irc == InternalResponseCodes.Approved && definition is not null)
            return definition.ResponseCode;
        throw Failure(request, irc, definition, trace);
    }

    private async Task<string> DecideAsync(
        ITransactionSession session, TransactionRequest request, string typeName, decimal? lira, Card? card, Trace trace)
    {
        // F22 opsiyonel; geldiyse ilk iki hanesi EntryMode tablosunda tanımlı olmalı (3. hane PIN yeteneği, karara girmez).
        EntryMode? entryMode = null;
        if (request.EntryMode is { } f22)
        {
            entryMode = f22.Length == 3 ? await parameters.FindEntryModeAsync(f22[..2]) : null;
            if (entryMode is null)
                return Decline(nameof(DecideAsync), request, InternalResponseCodes.EntryModeNotDefined, $"{IsoLog.Field(22, f22)} is not defined");
        }

        // İşyeri (F42) Merchant tablosunda olmalı ve Active olmalı; F18 geldiyse işyerinin MCC'si olmalı.
        // F42 sabit 15 karakterdir: kısa no sağdan boşlukla doldurulur, tabloda boşluksuz durur.
        if (request.MerchantId?.TrimEnd() is not { Length: > 0 } merchantId)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.MerchantIdMissing, $"{IsoLog.Label(42)} missing");
        var merchant = await session.GetMerchantAsync(merchantId);
        if (merchant is null)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.MerchantNotFound, $"{IsoLog.Field(42, merchantId)} not found");
        if (merchant.Status != ActiveMerchant)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.MerchantNotActive,
                $"{IsoLog.Field(42, merchantId)} is {merchant.Status}");
        if (request.MerchantType is { } mcc && mcc != merchant.MerchantType)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.MerchantTypeMismatch,
                $"{IsoLog.Field(18, mcc)} does not match merchant {merchantId} MCC {merchant.MerchantType}");

        if (card is null)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.CardNotFound, "card not found");
        trace.AccountNo = card.AccountNo;

        if (card.Status != CardStatus.Active)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.CardNotActive, $"card is {card.Status}");

        if (IsExpired(card.ExpiryDate) || !ExpiryMatches(card.ExpiryDate, request.CardExpiry))
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.CardExpired, $"card expired or {IsoLog.Field(14, request.CardExpiry)} does not match");

        // CVV2 kart fiziksel olarak yokken zorunlu; başka bir girişte de geldiyse kontrol edilir. Değeri asla loglanmaz.
        if ((entryMode is not null && CardNotPresentEntryModes.Contains(entryMode.Code)) || request.Cvv is not null)
        {
            if (request.Cvv is null)
                return Decline(nameof(DecideAsync), request, InternalResponseCodes.CvvMismatch, $"CVV2 required for {IsoLog.Field(22, request.EntryMode)}");
            if (!await CvvMatchesAsync(session, request.CardNumber, request.Cvv))
                return Decline(nameof(DecideAsync), request, InternalResponseCodes.CvvMismatch, "CVV2 does not match");
        }

        var account = await session.GetAccountAsync(card.AccountNo);
        trace.Email = account?.Email;
        if (account?.AccountStatus != ActiveAccount)
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.AccountNotActive,
                $"account {card.AccountNo} is {account?.AccountStatus ?? "missing"}");

        // Kredi kartı da bir hesaba bağlıdır: hesap Active değilse (yukarıda) kredi kartı da çalışmaz.
        var credit = card.CardType == CardTypes.Credit;

        // Taksit sadece kredi kartıyla satışta olur; tek çekim (1) her işlemde geçerlidir.
        if (request.InstallmentCount > 1 && !(credit && typeName == Sale))
            return Decline(nameof(DecideAsync), request, InternalResponseCodes.InstallmentsNotAllowed,
                $"{request.InstallmentCount} installments not allowed for {(credit ? "credit" : "debit")} card {typeName}");

        return (typeName, credit) switch
        {
            ("BalanceInquiry", _) => InternalResponseCodes.Approved,     // bakiye cevaba (F54) sonraki adımda eklenecek
            (Sale, false) => await SaleAsync(session, request, card, lira, entryMode),
            (Sale, true) => await CreditSaleAsync(session, request, card, lira, entryMode),
            (MoneyTransfer, false) => await TransferAsync(session, request, card, lira, trace),
            _ => Decline(nameof(DecideAsync), request, InternalResponseCodes.TransactionNotSupported,
                $"{typeName} is not supported for {(credit ? "credit" : "debit")} cards")
        };
    }

    // Limit, bakiye ve kartın son işlem tutarı hep TL karşılık (lira) üzerinden yürür; hesaplar TL'dir.
    private async Task<string> SaleAsync(ITransactionSession session, TransactionRequest request, Card card, decimal? lira, EntryMode? entryMode)
    {
        var (amount, declined) = CheckAmount(nameof(SaleAsync), request, lira);
        if (declined is not null)
            return declined;

        if (CheckContactless(nameof(SaleAsync), request, card, amount, entryMode) is { } contactlessDeclined)
            return contactlessDeclined;

        // Bakiye kontrolü ve düşme tek sorguda: aynı hesaba aynı anda gelen iki satış bakiyeyi eksiye düşüremez.
        if (!await session.TryDebitAsync(card.AccountNo, amount))
            return Decline(nameof(SaleAsync), request, InternalResponseCodes.InsufficientFunds, $"insufficient funds for TRY {amount}");

        await session.SetCardLastTransactionAsync(card.CardNumber, amount);
        logger.LogInformation("{Method}: debited TRY {Amount} from account {AccountNo} {Stan} {Pan}",
            nameof(SaleAsync), amount, card.AccountNo, IsoLog.Field(11, request.Stan), IsoLog.Field(2, request.CardNumber));
        return InternalResponseCodes.Approved;
    }

    // Kredi kartıyla satış: banka hesabının bakiyesine dokunulmaz, TL karşılığı kartın kullanılabilir limitinden düşer.
    // Taksitli satışta da toplam tutar bir kerede limitten düşer; taksitler ekstrelere bölünür (InstallmentCount kayıtta durur).
    private async Task<string> CreditSaleAsync(ITransactionSession session, TransactionRequest request, Card card, decimal? lira, EntryMode? entryMode)
    {
        var (amount, declined) = CheckAmount(nameof(CreditSaleAsync), request, lira);
        if (declined is not null)
            return declined;

        if (CheckContactless(nameof(CreditSaleAsync), request, card, amount, entryMode) is { } contactlessDeclined)
            return contactlessDeclined;

        if (await session.GetCreditLimitAsync(card.CardNumber) is null)
            return Decline(nameof(CreditSaleAsync), request, InternalResponseCodes.CreditLimitNotDefined, "credit limit not defined for card");

        // Limit kontrolü ve kullanılan limitin artması tek sorguda: aynı karta aynı anda gelen iki satış limiti aşamaz.
        if (!await session.TryUseCreditLimitAsync(card.CardNumber, amount))
            return Decline(nameof(CreditSaleAsync), request, InternalResponseCodes.InsufficientFunds, $"available credit limit is less than TRY {amount}");

        await session.SetCardLastTransactionAsync(card.CardNumber, amount);
        logger.LogInformation("{Method}: used TRY {Amount} of credit limit in {InstallmentCount} installment(s) {Stan} {Pan}",
            nameof(CreditSaleAsync), amount, request.InstallmentCount ?? 1, IsoLog.Field(11, request.Stan), IsoLog.Field(2, request.CardNumber));
        return InternalResponseCodes.Approved;
    }

    // Temassız olup olmadığı EntryMode.IsContactless'tan gelir; F22 yoksa temassız sayılmaz. Red IRC'si ya da null döner.
    private string? CheckContactless(string method, TransactionRequest request, Card card, decimal amount, EntryMode? entryMode)
    {
        if (entryMode is not { IsContactless: true })
            return null;
        if (!card.ContactlessAllowed)
            return Decline(method, request, InternalResponseCodes.ContactlessNotAllowed, "contactless not allowed for card");
        if (card.ContactlessLimit is { } limit && amount > limit)
            return Decline(method, request, InternalResponseCodes.ContactlessLimitExceeded, $"TRY {amount} exceeds contactless limit {limit}");
        return null;
    }

    // Karttan karta para transferi (sadece bu sistemdeki kartlar arası): F48'deki alıcı kartın hesabına, gönderenin hesabından.
    // Döviz de olabilir; iki hesap da TL olduğu için TL karşılığı düşülür ve eklenir. Temassız limit uygulanmaz,
    // alıcı kartın son kullanma tarihine bakılmaz. Gönderen kartın kontrolleri (kart, F14, CVV2, hesap) DecideAsync'te yapıldı.
    private async Task<string> TransferAsync(ITransactionSession session, TransactionRequest request, Card card, decimal? lira, Trace trace)
    {
        if (request.RecipientCardNumber is not { } recipientNumber || !IsCardNumber(recipientNumber))
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.RecipientCardInvalid, $"{IsoLog.Label(48)} missing or not a card number");

        var recipient = await session.GetCardAsync(recipientNumber);
        if (recipient is null)
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.RecipientCardNotFound,
                $"{IsoLog.Field(48, recipientNumber)} not found");
        if (recipient.AccountNo == card.AccountNo)
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.SameAccountTransfer,
                $"{IsoLog.Field(48, recipientNumber)} is on the same account {card.AccountNo}");

        var recipientAccount = await session.GetAccountAsync(recipient.AccountNo);
        (trace.RecipientPan, trace.RecipientAccountNo, trace.RecipientEmail) = (recipient.CardNumber, recipient.AccountNo, recipientAccount?.Email);
        if (recipientAccount?.AccountStatus != ActiveAccount)
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.RecipientAccountNotActive,
                $"recipient account {recipient.AccountNo} is {recipientAccount?.AccountStatus ?? "missing"}");

        var (amount, declined) = CheckAmount(nameof(TransferAsync), request, lira);
        if (declined is not null)
            return declined;

        if (!await session.TryTransferAsync(card.AccountNo, recipient.AccountNo, amount))
            return Decline(nameof(TransferAsync), request, InternalResponseCodes.InsufficientFunds, $"insufficient funds for TRY {amount}");

        await session.SetCardLastTransactionAsync(card.CardNumber, amount);
        logger.LogInformation("{Method}: transferred TRY {Amount} from account {FromAccountNo} to account {ToAccountNo} {Stan} {Pan} -> {RecipientPan}",
            nameof(TransferAsync), amount, card.AccountNo, recipient.AccountNo, IsoLog.Field(11, request.Stan),
            IsoLog.Field(2, request.CardNumber), IsoLog.Field(48, recipientNumber));
        return InternalResponseCodes.Approved;
    }

    // Satış ve transferde tutar: kur yoksa ya bu para birimi için TCMB kuru hiç yok (1004) ya da cache'te kur yok / bayat
    // (9001, sonra tekrar denenebilir); TL karşılığı 0 olamaz. Geçerliyse TL karşılığı, değilse red IRC'si döner.
    private (decimal Lira, string? Declined) CheckAmount(string method, TransactionRequest request, decimal? lira)
    {
        if (lira is not { } amount)
            return (0, ExchangeRateCache.IsSupported(request.CurrencyCode)
                ? Decline(method, request, InternalResponseCodes.ExchangeRateUnavailable, $"no fresh rate for {IsoLog.Field(49, request.CurrencyCode)}")
                : Decline(method, request, InternalResponseCodes.CurrencyNotSupported, $"{IsoLog.Field(49, request.CurrencyCode)} is not supported"));

        if (amount <= 0)
            return (0, Decline(method, request, InternalResponseCodes.ZeroAmount, "amount is zero"));
        return (amount, null);
    }

    // Red kararını sebebiyle loglar ve iç kodu (IRC) döner; F39 InternalResponseCode tablosundan bulunur.
    private string Decline(string method, TransactionRequest request, string irc, string reason)
    {
        logger.LogWarning("{Method}: declined {Stan} {Pan} - {Reason} -> IRC {Irc}",
            method, IsoLog.Field(11, request.Stan), IsoLog.Field(2, request.CardNumber), reason, irc);
        return irc;
    }

    private async Task<TransactionException> FailureAsync(TransactionRequest request, string irc, Trace trace) =>
        Failure(request, irc, await parameters.FindInternalResponseCodeAsync(irc), trace);

    // IRC tabloda tanımlıysa onun F39'u ve açıklaması; değilse hata loglanır ve 96: işlem asla tahminle onaylanmaz.
    private TransactionException Failure(TransactionRequest request, string irc, InternalResponseCode? definition, Trace trace)
    {
        if (definition is not null)
            return new TransactionException(definition.ResponseCode, irc, definition.Description, request.CardNumber, trace.Id, trace.AccountNo);

        logger.LogError("{Method}: IRC {Irc} is not defined in InternalResponseCode ({Stan} {Pan}) - responding {ResponseCode}",
            nameof(Failure), irc, IsoLog.Field(11, request.Stan), IsoLog.Field(2, request.CardNumber),
            IsoLog.Field(39, ResponseCodes.SystemMalfunction));
        return new TransactionException(
            ResponseCodes.SystemMalfunction, irc, $"IRC {irc} is not defined", request.CardNumber, trace.Id, trace.AccountNo);
    }

    // Tekrar gelen istek: ilk işlemin kararı aynen döner (ilk istek onaylandıysa tekrarı da onay alır ama para tekrar düşmez).
    // İlk karar redse tekrarı da aynı F39 ile TransactionException alır; kayıtta IRC tutulmadığı için açıklama F39'dan bulunur.
    private async Task<string> PreviousResponseAsync(TransactionRequest request, Guid id, Trace trace)
    {
        var previous = (await repository.GetPreviousAsync(id))!;   // kayıt sadece F39 yazılarak commit edildiği için boş olamaz
        trace.AccountNo = previous.AccountNo;

        logger.LogWarning("{Method}: duplicate request {Id} ({TerminalId} {Stan} {Pan}) - returning previous {ResponseCode}",
            nameof(PreviousResponseAsync), id, IsoLog.Field(41, request.TerminalId), IsoLog.Field(11, request.Stan),
            IsoLog.Field(2, request.CardNumber), IsoLog.Field(39, previous.ResponseCode));

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
    private async Task<bool> CvvMatchesAsync(ITransactionSession session, string cardNumber, string received) =>
        await session.GetCvvHashAsync(cardNumber) is { } stored
        && CryptographicOperations.FixedTimeEquals(stored, secrets.HashCvv(cardNumber, received));

    // ISO8583 F2 ile aynı kural: 1-19 rakam.
    private static bool IsCardNumber(string? value) => value is { Length: > 0 and <= 19 } && value.All(char.IsAsciiDigit);

    // POS F14'ü YYMM, Card tablosu MMYY formatında tutar.
    private static bool ExpiryMatches(string? cardExpiry, string? requestExpiry) =>
        cardExpiry is null || requestExpiry is null || requestExpiry == cardExpiry[2..] + cardExpiry[..2];
}
