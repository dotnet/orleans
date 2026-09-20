using System;
using System.Linq;
using Orleans;
using Orleans.Concurrency;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal static class DurableMessagingActivationValidator
{
    // The public attribute exposes the runtime's internal, sealed strategy type.
    private static readonly Type _statelessWorkerPlacementType = new StatelessWorkerAttribute().PlacementStrategy.GetType();

    public static void Validate(IGrainContext grainContext, GrainProperties properties, PlacementStrategy placementStrategy)
    {
        var grain = grainContext.GrainInstance
            ?? throw new InvalidOperationException("Durable Messaging activation requires an initialized grain instance.");
        var grainType = grain.GetType();
        if (placementStrategy.GetType() == _statelessWorkerPlacementType)
        {
            throw new InvalidOperationException(
                $"Durable Messaging requires one activation per grain identity, but grain type '{grainType}' is a stateless worker.");
        }

        if (properties.Properties.TryGetValue(WellKnownGrainTypeProperties.Reentrant, out var reentrant) && bool.Parse(reentrant)
            || properties.Properties.ContainsKey(WellKnownGrainTypeProperties.MayInterleavePredicate))
        {
            throw new InvalidOperationException(
                $"Durable Messaging requires non-reentrant grain execution, but grain type '{grainType}' enables interleaving.");
        }

        var grainInterfaces = grainType
            .GetInterfaces()
            .Where(static type => typeof(IGrain).IsAssignableFrom(type))
            .ToArray();
        var interleavableMethod = grainInterfaces
            .SelectMany(static type => type.GetInterfaces().Append(type))
            .Distinct()
            .SelectMany(static type => type.GetMethods())
            .FirstOrDefault(static method => method.IsDefined(typeof(AlwaysInterleaveAttribute), inherit: true));
        if (interleavableMethod is not null)
        {
            throw new InvalidOperationException(
                $"Durable Messaging grain type '{grainType}' implements interleavable method "
                + $"'{interleavableMethod.DeclaringType}.{interleavableMethod.Name}'.");
        }
    }
}
