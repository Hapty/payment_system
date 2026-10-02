using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.MtiProcessingCode için SQL sorguları (Dapper). Anahtar iki kolondan oluşur: (Mti, F3_ProcessingCode).
public sealed class MtiProcessingCodeRepository(SqlConnectionFactory db)
{
    private const string Columns = "Mti, F3_ProcessingCode, Otc, Ots";

    public async Task<IReadOnlyList<MtiProcessingCode>> GetAllAsync()
    {
        await using var connection = db.Create();
        var rows = await connection.QueryAsync<MtiProcessingCode>(
            $"SELECT {Columns} FROM dbo.MtiProcessingCode ORDER BY Mti, F3_ProcessingCode");
        return rows.AsList();
    }

    public async Task<MtiProcessingCode?> GetAsync(string mti, string processingCode)
    {
        await using var connection = db.Create();
        return await connection.QuerySingleOrDefaultAsync<MtiProcessingCode>(
            $"SELECT {Columns} FROM dbo.MtiProcessingCode WHERE Mti = @mti AND F3_ProcessingCode = @processingCode",
            new { mti, processingCode });
    }

    public async Task<WriteResult> CreateAsync(MtiProcessingCode code)
    {
        await using var connection = db.Create();
        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO dbo.MtiProcessingCode (Mti, F3_ProcessingCode, Otc, Ots) VALUES (@Mti, @F3_ProcessingCode, @Otc, @Ots)",
                code);
            return WriteResult.Ok;
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            return WriteResult.Duplicate;
        }
    }

    // Anahtar (Mti, F3) değişmez; sadece karşılık geldiği (Otc, Ots) güncellenir.
    public async Task<WriteResult> UpdateTargetAsync(string mti, string processingCode, string otc, string ots)
    {
        await using var connection = db.Create();
        var affected = await connection.ExecuteAsync(
            "UPDATE dbo.MtiProcessingCode SET Otc = @otc, Ots = @ots WHERE Mti = @mti AND F3_ProcessingCode = @processingCode",
            new { mti, processingCode, otc, ots });
        return affected == 0 ? WriteResult.NotFound : WriteResult.Ok;
    }

    public async Task<bool> DeleteAsync(string mti, string processingCode)
    {
        await using var connection = db.Create();
        return await connection.ExecuteAsync(
            "DELETE FROM dbo.MtiProcessingCode WHERE Mti = @mti AND F3_ProcessingCode = @processingCode",
            new { mti, processingCode }) > 0;
    }
}
