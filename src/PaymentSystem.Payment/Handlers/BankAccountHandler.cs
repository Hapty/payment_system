using PaymentSystem.Payment.Data;
using PaymentSystem.Payment.Models;

namespace PaymentSystem.Payment.Handlers;

// Banka hesabı CRUD işlemleri: BankAccountsController ile BankAccountRepository arasında.
public sealed class BankAccountHandler(IBankAccountRepository accounts, ILogger<BankAccountHandler> logger)
{
    public async Task<IReadOnlyList<BankAccount>> GetAllAsync()
    {
        var all = await accounts.GetAllAsync();
        logger.LogInformation("{Method}: returned {Count} accounts", nameof(GetAllAsync), all.Count);
        return all;
    }

    public async Task<BankAccount?> GetAsync(long accountNo)
    {
        var account = await accounts.GetAsync(accountNo);
        logger.LogInformation("{Method}: account {AccountNo} {Result}", nameof(GetAsync), accountNo, account is null ? "not found" : "found");
        return account;
    }

    public async Task<WriteResult> CreateAsync(BankAccount account)
    {
        var result = await accounts.CreateAsync(account);
        logger.LogInformation("{Method}: account {AccountNo} status {Status} balance {Balance} -> {Result}",
            nameof(CreateAsync), account.AccountNo, account.AccountStatus, account.Balance, result);
        return result;
    }

    // Durum ve e-posta değişir; bakiye hesap açılırken verilir, sonra yalnızca işlemlerle değişir.
    public async Task<WriteResult> UpdateAsync(long accountNo, string accountStatus, string? email)
    {
        var result = await accounts.UpdateAsync(accountNo, accountStatus, email);
        logger.LogInformation("{Method}: account {AccountNo} status {Status} email {Email} -> {Result}",
            nameof(UpdateAsync), accountNo, accountStatus, email is null ? "none" : "set", result);
        return result;
    }

    public async Task<bool> DeleteAsync(long accountNo)
    {
        var deleted = await accounts.DeleteAsync(accountNo);
        logger.LogInformation("{Method}: account {AccountNo} {Result}", nameof(DeleteAsync), accountNo, deleted ? "deleted" : "not found");
        return deleted;
    }
}
