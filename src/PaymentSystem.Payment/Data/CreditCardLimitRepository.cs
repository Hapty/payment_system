using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.CreditCardLimit için SQL sorguları (Dapper).
public sealed class CreditCardLimitRepository(SqlConnectionFactory db) : ICreditCardLimitRepository
{
    private const string Columns =
        "CardNumber, CreditLimit, UsedLimit, AvailableLimit, CashLimit, UsedCashLimit, StatementDay, DueDay";

    public async Task<IReadOnlyList<CreditCardLimit>> GetAllAsync()
    {
        await using var connection = db.Create();
        var rows = await connection.QueryAsync<CreditCardLimit>($"SELECT {Columns} FROM dbo.CreditCardLimit ORDER BY CardNumber");
        return rows.AsList();
    }

    public async Task<CreditCardLimit?> GetAsync(string cardNumber)
    {
        await using var connection = db.Create();
        return await connection.QuerySingleOrDefaultAsync<CreditCardLimit>(
            $"SELECT {Columns} FROM dbo.CreditCardLimit WHERE CardNumber = @cardNumber", new { cardNumber });
    }

    // Yeni limitte kullanılan tutarlar 0'dır; gelen değerleri yok sayılır. AvailableLimit DB'den geri okunur.
    public async Task<WriteResult> CreateAsync(CreditCardLimit limit)
    {
        await using var connection = db.Create();
        try
        {
            var created = await connection.QuerySingleAsync<CreditCardLimit>(
                """
                INSERT INTO dbo.CreditCardLimit (CardNumber, CreditLimit, CashLimit, StatementDay, DueDay)
                OUTPUT INSERTED.UsedLimit, INSERTED.AvailableLimit, INSERTED.UsedCashLimit
                VALUES (@CardNumber, @CreditLimit, @CashLimit, @StatementDay, @DueDay)
                """,
                limit);
            (limit.UsedLimit, limit.AvailableLimit, limit.UsedCashLimit) = (created.UsedLimit, created.AvailableLimit, created.UsedCashLimit);
            return WriteResult.Ok;
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            return WriteResult.Duplicate;
        }
    }

    public async Task<WriteResult> UpdateAsync(string cardNumber, CreditCardLimit input)
    {
        await using var connection = db.Create();
        var affected = await connection.ExecuteAsync(
            """
            UPDATE dbo.CreditCardLimit
            SET CreditLimit = @CreditLimit, CashLimit = @CashLimit, StatementDay = @StatementDay, DueDay = @DueDay
            WHERE CardNumber = @cardNumber
            """,
            new { cardNumber, input.CreditLimit, input.CashLimit, input.StatementDay, input.DueDay });
        return affected == 0 ? WriteResult.NotFound : WriteResult.Ok;
    }

    public async Task<bool> DeleteAsync(string cardNumber)
    {
        await using var connection = db.Create();
        return await connection.ExecuteAsync("DELETE FROM dbo.CreditCardLimit WHERE CardNumber = @cardNumber", new { cardNumber }) > 0;
    }
}
