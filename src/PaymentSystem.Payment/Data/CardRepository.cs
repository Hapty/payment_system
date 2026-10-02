using Dapper;
using Microsoft.Data.SqlClient;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.Card için SQL sorguları (Dapper). Cvv kolonu hiçbir sorguda yok (bkz. Card modeli).
public sealed class CardRepository(SqlConnectionFactory db)
{
    private const string Columns =
        "Id, CardNumber, AccountNo, CardType, ExpiryDate, LastTransactionDate, LastTransactionAmount, ContactlessAllowed, ContactlessLimit";

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
    // LastTransaction* alanları yeni kartta her zaman boştur, gelen değerleri yok sayılır.
    public async Task<WriteResult> CreateAsync(Card card)
    {
        await using var connection = db.Create();
        try
        {
            card.Id = await connection.ExecuteScalarAsync<int>(
                """
                INSERT INTO dbo.Card (CardNumber, AccountNo, CardType, ExpiryDate, ContactlessAllowed, ContactlessLimit)
                OUTPUT INSERTED.Id
                VALUES (@CardNumber, @AccountNo, @CardType, @ExpiryDate, @ContactlessAllowed, @ContactlessLimit)
                """,
                card);
            card.LastTransactionDate = null;
            card.LastTransactionAmount = null;
            return WriteResult.Ok;
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            return WriteResult.Duplicate;
        }
    }

    // LastTransaction* alanlarını işlem akışı yazar, bu yüzden burada güncellenmez.
    public async Task<WriteResult> UpdateAsync(int id, Card input)
    {
        await using var connection = db.Create();
        try
        {
            var affected = await connection.ExecuteAsync(
                """
                UPDATE dbo.Card
                SET CardNumber = @CardNumber, AccountNo = @AccountNo, CardType = @CardType, ExpiryDate = @ExpiryDate,
                    ContactlessAllowed = @ContactlessAllowed, ContactlessLimit = @ContactlessLimit
                WHERE Id = @Id
                """,
                new
                {
                    Id = id, input.CardNumber, input.AccountNo, input.CardType, input.ExpiryDate,
                    input.ContactlessAllowed, input.ContactlessLimit
                });
            return affected == 0 ? WriteResult.NotFound : WriteResult.Ok;
        }
        catch (SqlException ex) when (SqlErrors.IsDuplicateKey(ex))
        {
            return WriteResult.Duplicate;   // kart numarası başka bir kartta zaten var
        }
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var connection = db.Create();
        return await connection.ExecuteAsync("DELETE FROM dbo.Card WHERE Id = @id", new { id }) > 0;
    }
}
