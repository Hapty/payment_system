using Microsoft.Data.SqlClient;

namespace PaymentSystem.Payment.Data;

// Bir ekleme/güncelleme işleminin sonucu; handler controller'a iletir, controller HTTP koduna çevirir (204/201, 404, 409, 400).
public enum WriteResult
{
    Ok,
    NotFound,
    Duplicate,
    CardNumberChanged,      // kartın numarası değiştirilemez (CVV / PIN hash'i numaraya bağlı)
    NotCreditCard           // limit sadece CardType Credit olan karta tanımlanır
}

internal static class SqlErrors
{
    // 2627 = PRIMARY KEY / UNIQUE constraint ihlali, 2601 = unique index ihlali.
    // Önce "var mı?" diye sorup sonra eklemek yerine doğrudan ekleyip bu hatayı yakalıyoruz:
    // aynı anda gelen iki istekte de doğru çalışır (arada başka biri ekleyemez).
    public static bool IsDuplicateKey(SqlException ex) => ex.Number is 2627 or 2601;
}
