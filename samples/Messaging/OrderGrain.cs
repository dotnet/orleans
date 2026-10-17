using Orleans;
using Orleans.Messaging;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Messaging;

public interface IOrderGrain : IGrainWithStringKey, IMessagingGrain
{
    Task<MessageId> ReserveAsync(GrainId stock, MessageId commandId, int quantity);
    Task<DeliveryResult> ResubmitAsync(GrainId stock, MessageId commandId, int quantity);
}

public sealed class OrderGrain : Grain, IOrderGrain
{
    private readonly IInbox _inbox;
    private readonly IOutbox _outbox;
    private readonly IDurableStateManager _state;
    private readonly StockMessages _messages;

    private readonly CommittedReceiptsProbe _probe;
    private readonly IDurableList<ReservationOutcome> _receipts;

    public OrderGrain(
        IInbox inbox,
        IOutbox outbox,
        IDurableStateManager state,
        IJournaledStateManager journal,
        StockMessages messages,

        CommittedReceiptsProbe probe)
    {
        _inbox = inbox;
        _outbox = outbox;
        _state = state;
        _messages = messages;

        _probe = probe;
        _receipts = state.GetOrAddState<IDurableList<ReservationOutcome>>("receipts");
        journal.RegisterStateMachine("receipts-observer", new ReceiptsObserver(this));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.Register(_messages.Result, HandleResult);
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<MessageId> ReserveAsync(GrainId stock, MessageId commandId, int quantity)
    {
        _outbox.Send(_messages.Reserve, commandId, stock, new ReserveStock(quantity, this.GetGrainId()));
        await _state.WriteStateAsync(); // Ordinary callers explicitly await intent persistence.
        return commandId;
    }

    public async Task<DeliveryResult> ResubmitAsync(GrainId stock, MessageId commandId, int quantity)
    {
        var request = _messages.Reserve.Create(commandId, stock,
            new ReserveStock(quantity, this.GetGrainId()));
        // Explicit admission exposes the duplicate result after the original reply's ACK.
        return await GrainFactory.GetGrain<IMessagingExtension>(stock).DeliverAsync(request.ToInboxMessage());
    }

    private void HandleResult(ReservationOutcome outcome, IInboxHandlerContext context)
    {
        if (context.Message.Envelope.MessageId != new MessageId(outcome.CommandId.Key.CreateChildKey("result")))
        {
            throw new ArgumentException("The reply must identify the original reservation command.");
        }
        _receipts.Add(outcome);
        context.Complete();
    }

    // Captures receipts with each journal write and reports them after storage acknowledges it.
    private sealed class ReceiptsObserver(OrderGrain owner) : IStateMachine
    {
        private ReservationOutcome[] _captured = [];

        public void WritePendingEntries(JournalStreamWriter writer) => _captured = owner._receipts.ToArray();
        public void WriteSnapshot(JournalStreamWriter writer) => _captured = owner._receipts.ToArray();
        public void OnWriteCompleted() => owner._probe.OnAcknowledged(_captured);
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) { }
        public void Reset(JournalStreamWriter writer) { }
    }
}

// Arm each command's observer before sending; only its journal-acknowledged reply releases it.
public sealed class CommittedReceiptsProbe
{
    private readonly Dictionary<MessageId, TaskCompletionSource<ReservationOutcome[]>> _pending = [];
    private readonly object _lock = new();

    public Task<ReservationOutcome[]> WaitForAsync(MessageId commandId)
    {
        lock (_lock)
        {
            var completion = new TaskCompletionSource<ReservationOutcome[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(commandId, completion);
            return completion.Task;
        }
    }

    public void OnAcknowledged(ReservationOutcome[] receipts)
    {
        lock (_lock)
        {
            foreach (var receipt in receipts)
            {
                if (_pending.Remove(receipt.CommandId, out var completion))
                {
                    completion.TrySetResult(receipts);
                }
            }
        }
    }
}
