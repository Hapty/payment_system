using Microsoft.AspNetCore.Mvc;
using PaymentSystem.Payment.Models;
using PaymentSystem.Payment.Transactions;

namespace PaymentSystem.Payment.Controllers;

// Gate'in çağırdığı işlem akışı. İş sonucu (onay ya da red, örn. 51 yetersiz bakiye) her zaman 200 ile döner;
// 4xx/5xx sadece teknik hatalar içindir ve Gate onları F39=91'e çevirir.
[ApiController]
[Route("api/transactions")]
public class TransactionsController(TransactionProcessor processor) : ControllerBase
{
    [HttpPost]
    public async Task<TransactionResponse> Authorize(TransactionRequest request) =>
        new(await processor.ProcessAsync(request));
}
