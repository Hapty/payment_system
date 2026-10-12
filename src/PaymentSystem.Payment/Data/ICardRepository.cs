using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Data;

// dbo.Card erişimi. Handler'lar bu arayüze bağlıdır; SQL'i CardRepository yazar.
public interface ICardRepository
{
    Task<IReadOnlyList<Card>> GetAllAsync();

    Task<Card?> GetAsync(int id);

    Task<Card?> GetByNumberAsync(string cardNumber);

    Task<WriteResult> CreateAsync(Card card);

    Task<WriteResult> UpdateAsync(int id, Card input);

    Task<bool> DeleteAsync(int id);
}
