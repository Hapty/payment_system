using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.BankAccount için SQL sorguları (Dapper).
public sealed class BankAccountRepository(SqlConnectionFactory db)
{
    private const string Columns = "AccountNo, AccountStatus, Balance";

    public async Task<IReadOnlyList<BankAccount>> GetAllAsync()
    {
        await using var connection = db.Create();
        var rows = await connection.QueryAsync<BankAccount>($"SELECT {Columns} FROM dbo.BankAccount ORDER BY AccountNo");
        return rows.AsList();
    }

    public async Task<BankAccount?> GetAsync(string accountNo)
    {
        await using var connection = db.Create();
        return await connection.QuerySingleOrDefaultAsync<BankAccount>(
            $"SELECT {Columns} FROM dbo.BankAccount WHERE AccountNo = @accountNo", new { accountNo });
    }

    public async Task<WriteResult> CreateAsync(BankAccount account)
    {
        await using var connection = db.Create();
        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO dbo.BankAccount (AccountNo, AccountStatus, Balance) VALUES (@AccountNo, @AccountStatus, @Balance)",
                account);
            return WriteResult.Ok;
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            return WriteResult.Duplicate;
        }
    }

    // Sadece durum değişir. Bakiye hesap açılırken verilir; sonrasında yalnızca işlemlerle (ISO8583 akışı) değişir.
    public async Task<WriteResult> UpdateStatusAsync(string accountNo, string accountStatus)
    {
        await using var connection = db.Create();
        var affected = await connection.ExecuteAsync(
            "UPDATE dbo.BankAccount SET AccountStatus = @accountStatus WHERE AccountNo = @accountNo",
            new { accountNo, accountStatus });
        return affected == 0 ? WriteResult.NotFound : WriteResult.Ok;
    }

    public async Task<bool> DeleteAsync(string accountNo)
    {
        await using var connection = db.Create();
        return await connection.ExecuteAsync("DELETE FROM dbo.BankAccount WHERE AccountNo = @accountNo", new { accountNo }) > 0;
    }
}
