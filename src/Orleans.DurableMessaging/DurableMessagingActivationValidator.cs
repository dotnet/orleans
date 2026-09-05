using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal static class DurableMessagingActivationValidator
{
    public static void Validate(IGrainContext grainContext)
    {
        var grain = grainContext.GrainInstance
            ?? throw new InvalidOperationException("Durable Messaging activation requires an initialized grain instance.");
        var grainType = grain.GetType();
        if (grainType.IsDefined(typeof(StatelessWorkerAttribute), inherit: true))
        {
            throw new InvalidOperationException(
                $"Durable Messaging requires one activation per grain identity, but grain type '{grainType}' is a stateless worker.");
        }

        if (grainType.IsDefined(typeof(ReentrantAttribute), inherit: true)
            || grainType.IsDefined(typeof(MayInterleaveAttribute), inherit: true))
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
