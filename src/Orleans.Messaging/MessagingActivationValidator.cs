using System;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Messaging;

internal static class MessagingActivationValidator
{
    // The public attribute exposes the runtime's internal, sealed strategy type.
    private static readonly Type _statelessWorkerPlacementType = new StatelessWorkerAttribute().PlacementStrategy.GetType();

    public static void Validate(Type grainClass, PlacementStrategy placementStrategy)
    {
        if (placementStrategy.GetType() == _statelessWorkerPlacementType)
        {
            throw new InvalidOperationException(
                $"Messaging requires one activation per grain identity, but grain type '{grainClass}' is a stateless worker.");
        }
    }
}
