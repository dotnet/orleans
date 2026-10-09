using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;

namespace DurableMessaging;

public interface IOrderGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task<Guid> ReserveAsync(GrainId stock, HierarchicalKey operation, int quantity);
}

public sealed class OrderGrain : Grain, IOrderGrain, IInboxHandler, IJournaledStateHook, IDisposable
{
    private readonly IDurableOutbox _outbox;
    private readonly IDurableStateManager _state;
    private readonly IDurableList<ReservationOutcome> _receipts;
    private readonly Serializer<StockMessage> _serializer;
    private readonly CommittedReceiptsProbe _probe;
    private readonly ArcBufferWriter _encoder = new();
    private ReservationOutcome[] _captured = [];

    public OrderGrain(IDurableInbox inbox, IDurableOutbox outbox, IDurableStateManager state,
        IJournaledStateManager journal, Serializer<StockMessage> serializer, CommittedReceiptsProbe probe)
    {
        _outbox = outbox;
        _state = state;
        _serializer = serializer;
        _probe = probe;
        _receipts = state.GetOrAddState<IDurableList<ReservationOutcome>>("receipts");
        inbox.RegisterHandler(this);
        journal.Hooks.Add(this);
    }

    public async Task<Guid> ReserveAsync(GrainId stock, HierarchicalKey operation, int quantity)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        using var request = StockProtocol.Encode(_serializer, _encoder, this.GetGrainId(), stock,
            new ReserveStock(operation, quantity, this.GetGrainId()));
        _outbox.Send(request); // Send is synchronous and retains its own payload slice.
        await _state.WriteStateAsync(); // Ordinary callers explicitly await intent persistence.
        return request.MessageId;
    }

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (_serializer.Deserialize(context.Envelope.Payload) is not ReservationOutcome outcome)
        {
            throw new ArgumentException("The order grain only accepts reservation outcomes.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        _receipts.Add(outcome);
        context.Complete();
        return ValueTask.CompletedTask; // The runtime performs the write and acknowledgement.
    }

    // Capture the receipt snapshot before persistence and report it after acknowledgement.
    public ValueTask BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        _captured = _receipts.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask AfterOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        if (operation is JournaledStateOperation.Write or JournaledStateOperation.Snapshot)
        {
            _probe.OnAcknowledged(_captured);
        }
        _captured = [];
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _encoder.Dispose();
}

// The after-ack hook releases the host-local observer once both replies are committed.
public sealed class CommittedReceiptsProbe
{
    private readonly TaskCompletionSource<ReservationOutcome[]> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ReservationOutcome[]> Completion => _completion.Task;

    public void OnAcknowledged(ReservationOutcome[] receipts)
    {
        if (receipts.Length >= 2)
        {
            _completion.TrySetResult(receipts);
        }
    }
}
