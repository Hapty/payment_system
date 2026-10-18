using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// Finansal işlem akışının SQL sorguları (Dapper): DebitTransaction kaydı, bakiye ve kart güncellemesi.
public sealed class TransactionRepository(SqlConnectionFactory db) : ITransactionRepository
{
    public async Task<IDebitSession> BeginAsync()
    {
        var connection = db.Create();
        try
        {
            await connection.OpenAsync();
            return new DebitSession(connection, (SqlTransaction)await connection.BeginTransactionAsync());
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
            SELECT d.F39_ResponseCode AS ResponseCode, c.AccountNo
            FROM dbo.DebitTransaction d
            LEFT JOIN dbo.Card c ON c.CardNumber = d.F2_CardNo
            WHERE d.Guid = @id
            """,
            new { id });
    }

    private sealed class DebitSession(SqlConnection connection, SqlTransaction transaction) : IDebitSession
    {
        public async Task<bool> TryInsertAsync(DebitTransaction t)
        {
            try
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO dbo.DebitTransaction
                        (Guid, Mti, F2_CardNo, F3_ProcessingCode, Otc, Ots, F4_Amount, F49_CurrencyCode, LocalTxnAmount,
                         F12_TransactionTime, F13_TransactionDate, F14_CardExpiry, F18_MerchantCode, F22_EntryMode, F43_Description,
                         F48_RecipientCardNo)
                    VALUES
                        (@Id, @Mti, @CardNumber, @ProcessingCode, @Otc, @Ots, @Amount, @CurrencyCode, @LocalAmount,
                         @LocalTime, @TransactionDate, @CardExpiry, @MerchantType, @EntryMode, @CardAcceptor,
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

        public Task<Card?> GetCardAsync(string cardNumber) =>
            connection.QuerySingleOrDefaultAsync<Card>(
                "SELECT CardNumber, AccountNo, ExpiryDate, ContactlessAllowed, ContactlessLimit FROM dbo.Card WHERE CardNumber = @cardNumber",
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

        public Task SetCardLastTransactionAsync(string cardNumber, decimal amount) =>
            connection.ExecuteAsync(
                "UPDATE dbo.Card SET LastTransactionDate = SYSUTCDATETIME(), LastTransactionAmount = @amount WHERE CardNumber = @cardNumber",
                new { amount, cardNumber }, transaction);

        public Task SetResponseCodeAsync(Guid id, string responseCode) =>
            connection.ExecuteAsync(
                "UPDATE dbo.DebitTransaction SET F39_ResponseCode = @responseCode, UpdatedAt = SYSUTCDATETIME() WHERE Guid = @id",
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
