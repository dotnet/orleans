using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IPaymentGrain : IGrainWithGuidKey
{
    DurableTask<PaymentResult> AuthorizeAsync(decimal amount, string cardLast4);
}

// Authorizes the payment for one order, like eShop's PaymentProcessor.
// Card numbers ending in 0002 are declined. For card numbers ending in 0003, the gateway doesn't respond, so the
// authorization runs until the order's payment timeout cancels it.
public sealed class PaymentGrain : DurableGrain, IPaymentGrain
{
    public async DurableTask<PaymentResult> AuthorizeAsync(decimal amount, string cardLast4)
    {
        // A real gateway call goes here. The task runs until its result commits, at least once, so pass the task ID to
        // the gateway as its idempotency key.
        var context = DurableTask.Current;
        var latency = cardLast4 == "0003" ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(Random.Shared.Next(200, 800));
        await Task.Delay(latency, context.CancellationToken);
        return cardLast4 == "0002"
            ? new PaymentResult(false, "The card was declined.")
            : new PaymentResult(true, Reason: null);
    }
}
