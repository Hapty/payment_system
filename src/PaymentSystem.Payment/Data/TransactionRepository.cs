using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// Finansal işlem akışının SQL sorguları (Dapper): DebitTransaction / CreditTransaction kaydı, bakiye, kredi limiti ve kart güncellemesi.
public sealed class TransactionRepository(SqlConnectionFactory db) : ITransactionRepository
{
    public async Task<ITransactionSession> BeginAsync()
    {
        var connection = db.Create();
        try
        {
            await connection.OpenAsync();
            return new TransactionSession(connection, (SqlTransaction)await connection.BeginTransactionAsync());
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<PreviousResponse?> GetPreviousAsync(Guid id)
    {
        await using var connection = db.Create();
        return await connection.QuerySingleOrDefaultAsync<PreviousResponse>(
            """
            SELECT t.F39_ResponseCode AS ResponseCode, c.AccountNo
            FROM (SELECT Guid, F2_CardNo, F39_ResponseCode FROM dbo.DebitTransaction
                  UNION ALL
                  SELECT Guid, F2_CardNo, F39_ResponseCode FROM dbo.CreditTransaction) t
            LEFT JOIN dbo.Card c ON c.CardNumber = t.F2_CardNo
            WHERE t.Guid = @id
            """,
            new { id });
    }

    private sealed class TransactionSession(SqlConnection connection, SqlTransaction transaction) : ITransactionSession
    {
        // Bu oturumda kaydın açıldığı tablo; SetResponseCodeAsync oraya yazar.
        private string _table = "dbo.DebitTransaction";

        public async Task<bool> TryInsertAsync(DebitTransaction t)
        {
            _table = "dbo.DebitTransaction";
            try
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO dbo.DebitTransaction
                        (Guid, Mti, F2_CardNo, F3_ProcessingCode, Otc, Ots, F4_Amount, F49_CurrencyCode, LocalTxnAmount,
                         F12_TransactionTime, F13_TransactionDate, F14_CardExpiry, F18_MerchantCode, F22_EntryMode, F42_MerchantId, F43_Description,
                         F48_RecipientCardNo)
                    VALUES
                        (@Id, @Mti, @CardNumber, @ProcessingCode, @Otc, @Ots, @Amount, @CurrencyCode, @LocalAmount,
                         @LocalTime, @TransactionDate, @CardExpiry, @MerchantType, @EntryMode, @MerchantId, @CardAcceptor,
                         @RecipientCardNumber)
                    """,
                    t, transaction);
                return true;
            }
            catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
            {
                return false;
            }
        }

        public async Task<bool> TryInsertAsync(CreditTransaction t)
        {
            _table = "dbo.CreditTransaction";
            try
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO dbo.CreditTransaction
                        (Guid, Mti, F2_CardNo, F3_ProcessingCode, Otc, Ots, F4_Amount, F49_CurrencyCode, LocalTxnAmount,
                         F12_TransactionTime, F13_TransactionDate, F14_CardExpiry, F18_MerchantCode, F22_EntryMode, F42_MerchantId, F43_Description,
                         InstallmentCount)
                    VALUES
                        (@Id, @Mti, @CardNumber, @ProcessingCode, @Otc, @Ots, @Amount, @CurrencyCode, @LocalAmount,
                         @LocalTime, @TransactionDate, @CardExpiry, @MerchantType, @EntryMode, @MerchantId, @CardAcceptor,
                         @InstallmentCount)
                    """,
                    t, transaction);
                return true;
            }
            catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
            {
                return false;
            }
        }

        public Task<Merchant?> GetMerchantAsync(string merchantId) =>
            connection.QuerySingleOrDefaultAsync<Merchant>(
                "SELECT MerchantId, Name, MerchantType, Status FROM dbo.Merchant WHERE MerchantId = @merchantId",
                new { merchantId }, transaction);

        public Task<Card?> GetCardAsync(string cardNumber) =>
            connection.QuerySingleOrDefaultAsync<Card>(
                "SELECT CardNumber, AccountNo, CardType, ExpiryDate, ContactlessAllowed, ContactlessLimit, Status FROM dbo.Card WHERE CardNumber = @cardNumber",
                new { cardNumber }, transaction);

        public Task<byte[]?> GetCvvHashAsync(string cardNumber) =>
            connection.QuerySingleOrDefaultAsync<byte[]?>(
                "SELECT CvvHash FROM dbo.CardSecret WHERE CardNumber = @cardNumber", new { cardNumber }, transaction);

        public Task<BankAccount?> GetAccountAsync(long accountNo) =>
            connection.QuerySingleOrDefaultAsync<BankAccount>(
                "SELECT AccountNo, AccountStatus, Email FROM dbo.BankAccount WHERE AccountNo = @accountNo", new { accountNo }, transaction);

        // Aynı hesaba aynı anda gelen iki satış bakiyeyi eksiye düşüremez: kontrol ve düşme aynı UPDATE'te.
        public async Task<bool> TryDebitAsync(long accountNo, decimal amount) =>
            await connection.ExecuteAsync(
                "UPDATE dbo.BankAccount SET Balance = Balance - @amount WHERE AccountNo = @accountNo AND Balance >= @amount",
                new { amount, accountNo }, transaction) > 0;

        // İki hesabın satırı önce PK sırasıyla kilitlenir: A->B ve B->A transferleri aynı anda gelse de kilitler aynı sırayla
        // alınır, deadlock olmaz. Sonra düşme (bakiye kontrolüyle aynı UPDATE'te) ve ekleme.
        public async Task<bool> TryTransferAsync(long fromAccountNo, long toAccountNo, decimal amount)
        {
            await connection.ExecuteAsync(
                "SELECT AccountNo FROM dbo.BankAccount WITH (UPDLOCK, ROWLOCK) WHERE AccountNo IN (@fromAccountNo, @toAccountNo) ORDER BY AccountNo",
                new { fromAccountNo, toAccountNo }, transaction);
            if (!await TryDebitAsync(fromAccountNo, amount))
                return false;
            await connection.ExecuteAsync(
                "UPDATE dbo.BankAccount SET Balance = Balance + @amount WHERE AccountNo = @toAccountNo",
                new { amount, toAccountNo }, transaction);
            return true;
        }

        public Task<CreditCardLimit?> GetCreditLimitAsync(string cardNumber) =>
            connection.QuerySingleOrDefaultAsync<CreditCardLimit>(
                "SELECT CardNumber, CreditLimit, CashLimit, StatementDay, DueDay FROM dbo.CreditCardLimit WHERE CardNumber = @cardNumber",
                new { cardNumber }, transaction);

        // Aynı karta aynı anda gelen iki satış limiti aşamaz: kontrol ve artırma aynı UPDATE'te.
        public async Task<bool> TryUseCreditLimitAsync(string cardNumber, decimal amount) =>
            await connection.ExecuteAsync(
                "UPDATE dbo.CreditCardLimit SET UsedLimit = UsedLimit + @amount WHERE CardNumber = @cardNumber AND CreditLimit - UsedLimit >= @amount",
                new { amount, cardNumber }, transaction) > 0;

        public Task SetCardLastTransactionAsync(string cardNumber, decimal amount) =>
            connection.ExecuteAsync(
                "UPDATE dbo.Card SET LastTransactionDate = SYSUTCDATETIME(), LastTransactionAmount = @amount WHERE CardNumber = @cardNumber",
                new { amount, cardNumber }, transaction);

        public Task SetResponseCodeAsync(Guid id, string responseCode) =>
            connection.ExecuteAsync(
                $"UPDATE {_table} SET F39_ResponseCode = @responseCode, UpdatedAt = SYSUTCDATETIME() WHERE Guid = @id",
                new { responseCode, id }, transaction);

        public Task CommitAsync() => transaction.CommitAsync();

        // Commit edilmemiş transaction dispose edilince SQL Server onu geri alır.
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
