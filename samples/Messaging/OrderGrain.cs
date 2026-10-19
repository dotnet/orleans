using Orleans;
using Orleans.Messaging;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Messaging;

public interface IOrderGrain : IGrainWithStringKey, IMessagingGrain
{
    Task ReserveAsync(GrainId stock, MessageId commandId, int quantity);
    Task<ReservationOutcome> WaitForOutcomeAsync(MessageId commandId);
}

public sealed class OrderGrain : Grain, IOrderGrain
{
    private readonly IInbox _inbox;
    private readonly IOutbox _outbox;
    private readonly IDurableStateManager _state;
    private readonly StockMessages _messages;
    private readonly IDurableList<ReservationOutcome> _receipts;

    // Requests waiting for an outcome. Grain code runs one turn at a time, so a plain dictionary suffices.
    private readonly Dictionary<MessageId, TaskCompletionSource<ReservationOutcome>> _waiters = [];

    public OrderGrain(IInbox inbox, IOutbox outbox, IDurableStateManager state, StockMessages messages)
    {
        _inbox = inbox;
        _outbox = outbox;
        _state = state;
        _messages = messages;
        _receipts = state.GetOrAddState<IDurableList<ReservationOutcome>>("receipts");
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.Register(_messages.Result, HandleResult);
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task ReserveAsync(GrainId stock, MessageId commandId, int quantity)
    {
        _outbox.Send(_messages.Reserve, commandId, stock, new ReserveStock(quantity, this.GetGrainId()));

        // The command is delivered once this write is durable.
        await _state.WriteStateAsync();
    }

    public async Task<ReservationOutcome> WaitForOutcomeAsync(MessageId commandId)
    {
        var outcome = _receipts.FirstOrDefault(receipt => receipt.CommandId == commandId);
        if (outcome is null)
        {
            var waiter = new TaskCompletionSource<ReservationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(commandId, waiter);
            outcome = await waiter.Task;
        }

        // Any write includes the receipt which the reply's handler staged, so the outcome is durable once it returns.
        await _state.WriteStateAsync();
        return outcome;
    }

    private void HandleResult(ReservationOutcome outcome, IInboxHandlerContext context)
    {
        if (context.Message.Envelope.MessageId != new MessageId(outcome.CommandId.Key.CreateChildKey("result")))
        {
            context.Fail("The reply doesn't identify the reservation command it answers.");
            return;
        }

        _receipts.Add(outcome);
        context.Complete();
        if (_waiters.Remove(outcome.CommandId, out var waiter)) waiter.SetResult(outcome);
    }
}