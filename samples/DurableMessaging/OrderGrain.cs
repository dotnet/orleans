using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;

namespace DurableMessaging;

public interface IOrderGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task<HierarchicalKey> ReserveAsync(GrainId stock, HierarchicalKey commandId, int quantity);
    Task<DeliveryResult> ResubmitAsync(GrainId stock, HierarchicalKey commandId, int quantity);
}

public sealed class OrderGrain : Grain, IOrderGrain, IJournaledStateHook
{
    private readonly IDurableOutbox _outbox;
    private readonly IDurableStateManager _state;
    private readonly IDurableList<ReservationOutcome> _receipts;
    private readonly DurableMessageType<ReserveStock> _reserve;
    private readonly DurableMessageWriter _writer;
    private readonly CommittedReceiptsProbe _probe;
    private ReservationOutcome[] _captured = [];

    public OrderGrain(IDurableInbox inbox, IDurableOutbox outbox, IDurableStateManager state,
        IJournaledStateManager journal, DurableMessageWriter writer,
        [FromKeyedServices(StockProtocol.Reserve)] DurableMessageType<ReserveStock> reserve,
        [FromKeyedServices(StockProtocol.Result)] DurableMessageType<ReservationOutcome> result,
        CommittedReceiptsProbe probe)
    {
        _outbox = outbox;
        _state = state;
        _writer = writer;
        _reserve = reserve;
        _probe = probe;
        _receipts = state.GetOrAddState<IDurableList<ReservationOutcome>>("receipts");
        inbox.RegisterHandlers(routes => routes.Register(result, HandleResult));
        journal.Hooks.Add(this);
    }

    public async Task<HierarchicalKey> ReserveAsync(GrainId stock, HierarchicalKey commandId, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        using var request = _writer.Create(_reserve, commandId, stock,
            new ReserveStock(quantity, this.GetGrainId()));
        _outbox.Send(request); // Send is synchronous and retains its own payload slice.
        await _state.WriteStateAsync(); // Ordinary callers explicitly await intent persistence.
        return request.MessageId;
    }

    public async Task<DeliveryResult> ResubmitAsync(GrainId stock, HierarchicalKey commandId, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        using var request = _writer.Create(_reserve, commandId, stock,
            new ReserveStock(quantity, this.GetGrainId()));
        // Explicit admission exposes the duplicate result after the original reply's ACK.
        return await GrainFactory.GetGrain<IDurableInboxExtension>(stock).DeliverAsync(request);
    }

    private ValueTask HandleResult(ReservationOutcome outcome, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (context.Envelope.MessageId != outcome.CommandId.CreateChildKey("result"))
        {
            throw new ArgumentException("The reply must identify the original reservation command.");
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
}

// The after-ack hook releases the host-local observer when the original reply is committed.
public sealed class CommittedReceiptsProbe
{
    private readonly TaskCompletionSource<ReservationOutcome[]> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ReservationOutcome[]> Completion => _completion.Task;

    public void OnAcknowledged(ReservationOutcome[] receipts)
    {
        if (receipts.Length >= 1)
        {
            _completion.TrySetResult(receipts);
        }
    }
}
