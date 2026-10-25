using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.Card için SQL sorguları (Dapper). CVV ve PIN'in hash'leri dbo.CardSecret'tadır, sadece kart oluşturulurken yazılır.
public sealed class CardRepository(SqlConnectionFactory db) : ICardRepository
{
    private const string Columns =
        "Id, CardNumber, AccountNo, CardType, ExpiryDate, LastTransactionDate, LastTransactionAmount, ContactlessAllowed, ContactlessLimit, Status";

    public async Task<IReadOnlyList<Card>> GetAllAsync()
    {
        await using var connection = db.Create();
        var rows = await connection.QueryAsync<Card>($"SELECT {Columns} FROM dbo.Card ORDER BY Id");
        return rows.AsList();
    }

    public async Task<Card?> GetAsync(int id)
    {
        await using var connection = db.Create();
        return await connection.QuerySingleOrDefaultAsync<Card>($"SELECT {Columns} FROM dbo.Card WHERE Id = @id", new { id });
    }

    public async Task<Card?> GetByNumberAsync(string cardNumber)
    {
        await using var connection = db.Create();
        return await connection.QuerySingleOrDefaultAsync<Card>(
            $"SELECT {Columns} FROM dbo.Card WHERE CardNumber = @cardNumber", new { cardNumber });
    }

    // Id'yi veritabanı verir (IDENTITY); OUTPUT INSERTED.Id ile aynı sorguda geri okunur.
    // Kart ve CVV / PIN hash'leri tek transaction'da yazılır: CardSecret'ı olmayan kart oluşmaz.
    // LastTransaction* alanları yeni kartta her zaman boştur, gelen değerleri yok sayılır.
    public async Task<WriteResult> CreateAsync(Card card, CardSecret secret)
    {
        await using var connection = db.Create();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            card.Id = await connection.ExecuteScalarAsync<int>(
                """
                INSERT INTO dbo.Card (CardNumber, AccountNo, CardType, ExpiryDate, ContactlessAllowed, ContactlessLimit, Status)
                OUTPUT INSERTED.Id
                VALUES (@CardNumber, @AccountNo, @CardType, @ExpiryDate, @ContactlessAllowed, @ContactlessLimit, @Status)
                """,
                card, transaction);
            await connection.ExecuteAsync(
                "INSERT INTO dbo.CardSecret (CardNumber, CvvHash, PinHash) VALUES (@CardNumber, @CvvHash, @PinHash)",
                secret, transaction);
            await transaction.CommitAsync();
            card.LastTransactionDate = null;
            card.LastTransactionAmount = null;
            return WriteResult.Ok;
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            return WriteResult.Duplicate;
        }
    }

    // Kart numarası ve LastTransaction* alanları güncellenmez: numara değiştirilemez (CardHandler kontrol eder),
    // LastTransaction* alanlarını işlem akışı yazar.
    public async Task<WriteResult> UpdateAsync(int id, Card input)
    {
        await using var connection = db.Create();
        var affected = await connection.ExecuteAsync(
            """
            UPDATE dbo.Card
            SET AccountNo = @AccountNo, CardType = @CardType, ExpiryDate = @ExpiryDate,
                ContactlessAllowed = @ContactlessAllowed, ContactlessLimit = @ContactlessLimit, Status = @Status
            WHERE Id = @Id
            """,
            new { Id = id, input.AccountNo, input.CardType, input.ExpiryDate, input.ContactlessAllowed, input.ContactlessLimit, input.Status });
        return affected == 0 ? WriteResult.NotFound : WriteResult.Ok;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var connection = db.Create();
        return await connection.ExecuteAsync("DELETE FROM dbo.Card WHERE Id = @id", new { id }) > 0;
    }
}
