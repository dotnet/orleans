using Orleans.Journaling;
using Orleans.Messaging;

namespace OrderProcessing.Grains;

public interface IPaymentGrain : IGrainWithGuidKey;

// Authorizes the payment for one order, like eShop's PaymentProcessor.
// Card numbers ending in 0002 are declined.
public sealed class PaymentGrain : DurableGrain, IPaymentGrain
{
    private readonly IOutbox _outbox;
    private readonly OrderMessages _messages;

    public PaymentGrain(IInbox inbox, IOutbox outbox, OrderMessages messages)
    {
        _outbox = outbox;
        _messages = messages;
        inbox.Register(messages.AuthorizePayment, HandleAuthorizeAsync);
    }

    private async ValueTask HandleAuthorizeAsync(AuthorizePayment request, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        // A real gateway call goes here. The handler runs at least once, so pass the message ID to the
        // gateway as its idempotency key.
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(200, 800)), cancellationToken);
        var result = request.CardLast4 == "0002"
            ? new PaymentResult(false, "The card was declined.")
            : new PaymentResult(true, Reason: null);

        _outbox.SendReply(_messages.PaymentResult, context, request.ReplyTo, result);
        context.Complete();
    }
}
