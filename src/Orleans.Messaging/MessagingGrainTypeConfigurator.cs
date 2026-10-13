using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Messaging;

internal sealed class MessagingGrainTypeConfigurator(GrainClassMap grainClasses, IServiceProvider services) : IConfigureGrainTypeComponents
{
    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        if (grainClasses.TryGetGrainClass(grainType, out var grainClass)
            && (typeof(DurableGrain).IsAssignableFrom(grainClass) || typeof(IMessagingGrain).IsAssignableFrom(grainClass)))
        {
            var placementStrategy = shared.PlacementStrategy;
            shared.SetComponent(new MessagingShared(services, grainType));
            shared.AddActivationSetup(context =>
            {
                MessagingActivationValidator.Validate(grainClass, placementStrategy);
                var activationServices = context.ActivationServices;
                var inbox = activationServices.GetRequiredService<Inbox>();
                context.GetComponent<IGrainExtensionBinder>()!.GetOrSetExtension<Inbox, IInboxExtension>(() => inbox);
            });
        }
    }
}
