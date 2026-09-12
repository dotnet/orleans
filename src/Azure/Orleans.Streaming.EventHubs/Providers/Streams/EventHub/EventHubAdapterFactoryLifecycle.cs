using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Streaming.EventHubs;

internal sealed class EventHubAdapterFactoryLifecycle<TLifecycle>(
    IServiceProvider services,
    string name) : ILifecycleParticipant<TLifecycle>
    where TLifecycle : ILifecycleObservable
{
    public void Participate(TLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            $"{nameof(EventHubAdapterFactory)}.{name}",
            ServiceLifecycleStage.ApplicationServices,
            static _ => Task.CompletedTask,
            CloseAsync);
    }

    private async Task CloseAsync(CancellationToken cancellationToken)
    {
        var factory = services.GetRequiredKeyedService<IQueueAdapterFactory>(name);
        if (factory is not EventHubAdapterFactory eventHubFactory)
        {
            return;
        }

        var closeTask = eventHubFactory.CloseAsync(CancellationToken.None);
        try
        {
            await closeTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            closeTask.Ignore();
        }
    }
}
