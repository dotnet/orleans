using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationSystemTarget : SystemTarget, IDisseminationSystemTarget, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly DisseminationService _service;

    public DisseminationSystemTarget(DisseminationService service, SystemTargetShared shared)
        : base(Constants.DisseminationSystemTargetType, shared)
    {
        _service = service;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public Task PushGossip(DisseminationGossipBatch batch, CancellationToken cancellationToken) =>
        this.RunOrQueueTask(() => _service.ReceiveGossip(batch, cancellationToken));

    public async Task<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken)
    {
        DisseminationAntiEntropyResponse? response = null;
        await this.RunOrQueueTask(async () => response = await _service.ReceiveAntiEntropy(request, cancellationToken));
        return response!;
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(DisseminationSystemTarget),
            ServiceLifecycleStage.RuntimeServices,
            _service.StartAsync,
            _service.StopAsync);
    }
}
