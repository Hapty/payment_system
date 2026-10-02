using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.TransactionType için SQL sorguları (Dapper). Anahtar iki kolondan oluşur: (Otc, Ots).
public sealed class TransactionTypeRepository(SqlConnectionFactory db)
{
    private const string Columns = "Otc, Ots, Name";

    public async Task<IReadOnlyList<TransactionType>> GetAllAsync()
    {
        await using var connection = db.Create();
        var rows = await connection.QueryAsync<TransactionType>($"SELECT {Columns} FROM dbo.TransactionType ORDER BY Otc, Ots");
        return rows.AsList();
    }

    public async Task<TransactionType?> GetAsync(string otc, string ots)
    {
        await using var connection = db.Create();
        return await connection.QuerySingleOrDefaultAsync<TransactionType>(
            $"SELECT {Columns} FROM dbo.TransactionType WHERE Otc = @otc AND Ots = @ots", new { otc, ots });
    }

    public async Task<WriteResult> CreateAsync(TransactionType type)
    {
        await using var connection = db.Create();
        try
        {
            await connection.ExecuteAsync("INSERT INTO dbo.TransactionType (Otc, Ots, Name) VALUES (@Otc, @Ots, @Name)", type);
            return WriteResult.Ok;
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            return WriteResult.Duplicate;
        }
    }

    // Anahtar (Otc, Ots) değişmez; sadece isim güncellenir.
    public async Task<WriteResult> UpdateNameAsync(string otc, string ots, string name)
    {
        await using var connection = db.Create();
        var affected = await connection.ExecuteAsync(
            "UPDATE dbo.TransactionType SET Name = @name WHERE Otc = @otc AND Ots = @ots", new { otc, ots, name });
        return affected == 0 ? WriteResult.NotFound : WriteResult.Ok;
    }

    public async Task<bool> DeleteAsync(string otc, string ots)
    {
        await using var connection = db.Create();
        return await connection.ExecuteAsync("DELETE FROM dbo.TransactionType WHERE Otc = @otc AND Ots = @ots", new { otc, ots }) > 0;
    }
}
