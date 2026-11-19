using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Messaging;

internal sealed class MessagingGrainTypeConfigurator(GrainClassMap grainClasses, IServiceProvider services) : IConfigureGrainTypeComponents
{
    // The public attribute exposes the runtime's internal, sealed strategy type.
    private static readonly Type StatelessWorkerPlacementType = new StatelessWorkerAttribute().PlacementStrategy.GetType();

    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        if (!grainClasses.TryGetGrainClass(grainType, out var grainClass)
            || !(typeof(DurableGrain).IsAssignableFrom(grainClass) || typeof(IMessagingGrain).IsAssignableFrom(grainClass)))
        {
            return;
        }

        // Messaging requires one activation per grain identity, so every activation of a stateless worker fails.
        var isStatelessWorker = shared.PlacementStrategy.GetType() == StatelessWorkerPlacementType;
        shared.SetComponent(new MessagingShared(services, grainType));
        shared.AddActivationSetup(context =>
        {
            if (isStatelessWorker)
            {
                throw new InvalidOperationException(
                    $"Messaging requires one activation per grain identity, but grain type '{grainClass}' is a stateless worker.");
            }

            var inbox = context.ActivationServices.GetRequiredService<Inbox>();
            context.GetComponent<IGrainExtensionBinder>()!.GetOrSetExtension<Inbox, IMessagingExtension>(() => inbox);
        });
    }
}
