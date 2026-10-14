using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.Card erişimi. Handler'lar bu arayüze bağlıdır; SQL'i CardRepository yazar.
public interface ICardRepository
{
    Task<IReadOnlyList<Card>> GetAllAsync();

    Task<Card?> GetAsync(int id);

    Task<Card?> GetByNumberAsync(string cardNumber);

    // Kart ve CVV / PIN hash'leri birlikte yazılır; biri yazılamazsa hiçbiri yazılmaz.
    Task<WriteResult> CreateAsync(Card card, CardSecret secret);

    // Kart numarasını değiştirmez.
    Task<WriteResult> UpdateAsync(int id, Card input);

    Task<bool> DeleteAsync(int id);
}
