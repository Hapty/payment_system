using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Transactions;

// Gate'ten gelen bir finansal isteği karara bağlar ve F39 cevap kodunu döner.
// İşlem kaydı, bakiye ve kart güncellemesi tek bir SQL transaction'ında yapılır: ya hepsi yazılır ya hiçbiri.
public sealed class TransactionProcessor(SqlConnectionFactory db, TimeProvider clock, ILogger<TransactionProcessor> logger)
{
    private const string TurkishLira = "949";
    private const string ActiveAccount = "Active";

    private sealed record RoutedType(string Otc, string Ots, string Name);

    public async Task<string> ProcessAsync(TransactionRequest request)
    {
        await using var connection = db.Create();
        await connection.OpenAsync();

        // MTI + F3 -> (Otc, Ots) -> işlem adı. Tanımsız bir işlem için kayıt açılmaz: para hareketi yok, tekrarı da aynı cevabı alır.
        var type = await connection.QuerySingleOrDefaultAsync<RoutedType>(
            """
            SELECT m.Otc, m.Ots, t.Name
            FROM dbo.MtiProcessingCode m
            JOIN dbo.TransactionType t ON t.Otc = m.Otc AND t.Ots = m.Ots
            WHERE m.Mti = @Mti AND m.F3_ProcessingCode = @ProcessingCode
            """,
            new { request.Mti, request.ProcessingCode });
        if (type is null)
        {
            logger.LogWarning("No transaction type for MTI {Mti} F3 {ProcessingCode} (TID {TerminalId} STAN {Stan})",
                request.Mti, request.ProcessingCode, request.TerminalId, request.Stan);
            return ResponseCodes.InvalidTransaction;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        // Önce kaydı açıyoruz: aynı (F41, F11, F7) ile ikinci istek unique index'e takılır. Aynı anda gelirse ilk transaction
        // bitene kadar bekler, sonra hata alır; böylece aynı işlem asla iki kez karara bağlanmaz.
        Guid id;
        try
        {
            id = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO dbo.DebitTransaction
                    (Mti, F2_CardNo, F3_ProcessingCode, Otc, Ots, F4_Amount, F49_CurrencyCode, LocalTxnAmount,
                     F7_TransmissionDateTime, F11_Stan, F12_TransactionTime, F13_TransactionDate, F14_CardExpiry,
                     F18_MerchantCode, F22_EntryMode, F37_Rrn, F41_TerminalId, F42_MerchantId, F43_Description)
                OUTPUT INSERTED.Guid
                VALUES
                    (@Mti, @CardNumber, @ProcessingCode, @Otc, @Ots, @Amount, @CurrencyCode, @LocalAmount,
                     @TransmissionDateTime, @Stan, @LocalTime, @TransactionDate, @CardExpiry,
                     @MerchantType, @EntryMode, @Rrn, @TerminalId, @MerchantId, @CardAcceptor)
                """,
                new
                {
                    request.Mti, request.CardNumber, request.ProcessingCode, type.Otc, type.Ots,
                    Amount = ToAmount(request),
                    request.CurrencyCode,
                    LocalAmount = request.CurrencyCode == TurkishLira ? ToAmount(request) : (decimal?)null,  // kur çevrimi henüz yok
                    request.TransmissionDateTime, request.Stan, request.LocalTime,
                    TransactionDate = ToTransactionDate(request.LocalDate),
                    request.CardExpiry, request.MerchantType, request.EntryMode, request.Rrn,
                    request.TerminalId, request.MerchantId, CardAcceptor = request.CardAcceptor?.TrimEnd()
                },
                transaction);
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            await transaction.RollbackAsync();
            return await PreviousResponseAsync(connection, request);
        }

        var responseCode = await DecideAsync(connection, transaction, request, type.Name);

        await connection.ExecuteAsync(
            "UPDATE dbo.DebitTransaction SET F39_ResponseCode = @responseCode, UpdatedAt = SYSUTCDATETIME() WHERE Guid = @id",
            new { responseCode, id }, transaction);
        await transaction.CommitAsync();

        logger.LogInformation("{Type} TID {TerminalId} STAN {Stan} amount {Amount} {Currency} -> F39 {ResponseCode}",
            type.Name, request.TerminalId, request.Stan, ToAmount(request), request.CurrencyCode, responseCode);
        return responseCode;
    }

    private async Task<string> DecideAsync(SqlConnection connection, SqlTransaction transaction, TransactionRequest request, string typeName)
    {
        var card = await connection.QuerySingleOrDefaultAsync<Card>(
            "SELECT CardNumber, AccountNo, ExpiryDate, ContactlessAllowed, ContactlessLimit FROM dbo.Card WHERE CardNumber = @CardNumber",
            new { request.CardNumber }, transaction);
        if (card is null)
            return ResponseCodes.InvalidCardNumber;

        if (IsExpired(card.ExpiryDate) || !ExpiryMatches(card.ExpiryDate, request.CardExpiry))
            return ResponseCodes.ExpiredCard;

        var accountStatus = await connection.ExecuteScalarAsync<string?>(
            "SELECT AccountStatus FROM dbo.BankAccount WHERE AccountNo = @AccountNo", new { card.AccountNo }, transaction);
        if (accountStatus != ActiveAccount)
            return ResponseCodes.RestrictedCard;

        return typeName switch
        {
            "BalanceInquiry" => ResponseCodes.Approved,     // bakiye cevaba (F54) sonraki adımda eklenecek
            "Sale" => await SaleAsync(connection, transaction, request, card),
            // MoneyTransfer: alıcı kart numarası henüz ISO mesajında / Gate sözleşmesinde yok.
            _ => ResponseCodes.InvalidTransaction
        };
    }

    private static async Task<string> SaleAsync(SqlConnection connection, SqlTransaction transaction, TransactionRequest request, Card card)
    {
        if (request.CurrencyCode != TurkishLira)
            return ResponseCodes.InvalidTransaction;

        var amount = ToAmount(request);
        if (amount <= 0)
            return ResponseCodes.InvalidAmount;

        if (IsContactless(request.EntryMode))
        {
            if (!card.ContactlessAllowed)
                return ResponseCodes.NotPermittedToCardholder;
            if (card.ContactlessLimit is { } limit && amount > limit)
                return ResponseCodes.ContactlessLimitExceeded;
        }

        // Bakiye kontrolü ve düşme tek sorguda: aynı hesaba aynı anda gelen iki satış bakiyeyi eksiye düşüremez.
        var debited = await connection.ExecuteAsync(
            "UPDATE dbo.BankAccount SET Balance = Balance - @amount WHERE AccountNo = @AccountNo AND Balance >= @amount",
            new { amount, card.AccountNo }, transaction);
        if (debited == 0)
            return ResponseCodes.InsufficientFunds;

        await connection.ExecuteAsync(
            "UPDATE dbo.Card SET LastTransactionDate = SYSUTCDATETIME(), LastTransactionAmount = @amount WHERE CardNumber = @CardNumber",
            new { amount, card.CardNumber }, transaction);
        return ResponseCodes.Approved;
    }

    // Tekrar gelen istek: ilk işlemin kararı aynen döner (ilk istek onaylandıysa tekrarı da "00" alır ama para tekrar düşmez).
    private async Task<string> PreviousResponseAsync(SqlConnection connection, TransactionRequest request)
    {
        var previous = await connection.ExecuteScalarAsync<string>(
            """
            SELECT F39_ResponseCode FROM dbo.DebitTransaction
            WHERE F41_TerminalId = @TerminalId AND F11_Stan = @Stan AND F7_TransmissionDateTime = @TransmissionDateTime
            """,
            new { request.TerminalId, request.Stan, request.TransmissionDateTime });

        logger.LogWarning("Duplicate request TID {TerminalId} STAN {Stan} F7 {F7} - returning previous F39 {ResponseCode}",
            request.TerminalId, request.Stan, request.TransmissionDateTime, previous);
        return previous!;   // kayıt sadece F39 yazılarak commit edildiği için boş olamaz
    }

    private static decimal ToAmount(TransactionRequest request) => request.AmountMinor / 100m;

    // F22'nin ilk iki hanesi: 07 = temassız çip, 91 = temassız manyetik şerit.
    private static bool IsContactless(string? entryMode) => entryMode is ['0', '7', _] or ['9', '1', _];

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
