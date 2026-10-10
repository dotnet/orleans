using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DurableMessagingClusterCollection
{
    public const string Name = "Durable messaging cluster";
}
