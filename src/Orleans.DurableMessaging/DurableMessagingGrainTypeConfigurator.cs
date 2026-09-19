using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingGrainTypeConfigurator(GrainClassMap grainClasses) : IConfigureGrainTypeComponents
{
    public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
    {
        if (!grainClasses.TryGetGrainClass(grainType, out var grainClass))
        {
            throw new InvalidOperationException($"No grain implementation is registered for '{grainType}'.");
        }

        if (typeof(DurableGrain).IsAssignableFrom(grainClass) || typeof(IDurableMessagingGrain).IsAssignableFrom(grainClass))
        {
            shared.AddActivationSetup(context =>
            {
                DurableMessagingActivationValidator.Validate(context, properties);
                var services = context.ActivationServices;
                _ = services.GetRequiredService<IJournaledStateManager>();
                _ = services.GetRequiredService<IDurableInbox>();
                _ = services.GetRequiredService<IDurableOutbox>();
                _ = services.GetRequiredService<DurableInboxExtension>();
            });
        }
    }
}
