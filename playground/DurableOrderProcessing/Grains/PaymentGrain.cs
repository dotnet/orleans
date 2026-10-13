using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IPaymentGrain : IGrainWithGuidKey
{
    DurableTask<PaymentResult> AuthorizeAsync(decimal amount, string cardLast4);
}

// Authorizes the payment for one order, like eShop's PaymentProcessor.
// Card numbers ending in 0002 are declined.
public sealed class PaymentGrain : DurableGrain, IPaymentGrain
{
    public async DurableTask<PaymentResult> AuthorizeAsync(decimal amount, string cardLast4)
    {
        // A real gateway call goes here. The task runs until its result commits, at least once, so pass the task ID to
        // the gateway as its idempotency key.
        var context = DurableExecutionContext.Current!;
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(200, 800)), context.CancellationToken);
        return cardLast4 == "0002"
            ? new PaymentResult(false, "The card was declined.")
            : new PaymentResult(true, Reason: null);
    }
}
