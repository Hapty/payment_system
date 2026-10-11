using Dapper;

namespace PaymentSystem.Payment.Data;

// Tanım tablolarının SQL sorguları (Dapper). Tablolar küçük: her biri tek sorguda tamamen okunur.
public sealed class ParameterRepository(SqlConnectionFactory db) : IParameterRepository
{
    public async Task<IReadOnlyList<TransactionRoute>> GetRoutesAsync()
    {
        await using var connection = db.Create();
        var rows = await connection.QueryAsync<TransactionRoute>(
            """
            SELECT m.Mti, m.F3_ProcessingCode AS ProcessingCode, m.Otc, m.Ots, t.Name
            FROM dbo.MtiProcessingCode m
            JOIN dbo.TransactionType t ON t.Otc = m.Otc AND t.Ots = m.Ots
            """);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<EntryMode>> GetEntryModesAsync()
    {
        await using var connection = db.Create();
        var rows = await connection.QueryAsync<EntryMode>("SELECT Code, Name, IsContactless FROM dbo.EntryMode");
        return rows.AsList();
    }
}
