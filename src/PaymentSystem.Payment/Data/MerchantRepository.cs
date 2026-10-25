using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.Merchant için SQL sorguları (Dapper).
public sealed class MerchantRepository(SqlConnectionFactory db) : IMerchantRepository
{
    private const string Columns = "MerchantId, Name, MerchantType, Status, CreatedAt";

    public async Task<IReadOnlyList<Merchant>> GetAllAsync()
    {
        await using var connection = db.Create();
        var rows = await connection.QueryAsync<Merchant>($"SELECT {Columns} FROM dbo.Merchant ORDER BY MerchantId");
        return rows.AsList();
    }

    public async Task<Merchant?> GetAsync(string merchantId)
    {
        await using var connection = db.Create();
        return await connection.QuerySingleOrDefaultAsync<Merchant>(
            $"SELECT {Columns} FROM dbo.Merchant WHERE MerchantId = @merchantId", new { merchantId });
    }

    // CreatedAt DB'de verilir ve modele geri yazılır.
    public async Task<WriteResult> CreateAsync(Merchant merchant)
    {
        await using var connection = db.Create();
        try
        {
            merchant.CreatedAt = await connection.ExecuteScalarAsync<DateTime>(
                """
                INSERT INTO dbo.Merchant (MerchantId, Name, MerchantType, Status)
                OUTPUT INSERTED.CreatedAt
                VALUES (@MerchantId, @Name, @MerchantType, @Status)
                """,
                merchant);
            return WriteResult.Ok;
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            return WriteResult.Duplicate;
        }
    }

    // Anahtar (MerchantId) ve CreatedAt değişmez.
    public async Task<WriteResult> UpdateAsync(string merchantId, string name, string merchantType, string status)
    {
        await using var connection = db.Create();
        var affected = await connection.ExecuteAsync(
            "UPDATE dbo.Merchant SET Name = @name, MerchantType = @merchantType, Status = @status WHERE MerchantId = @merchantId",
            new { merchantId, name, merchantType, status });
        return affected == 0 ? WriteResult.NotFound : WriteResult.Ok;
    }

    public async Task<bool> DeleteAsync(string merchantId)
    {
        await using var connection = db.Create();
        return await connection.ExecuteAsync("DELETE FROM dbo.Merchant WHERE MerchantId = @merchantId", new { merchantId }) > 0;
    }
}
