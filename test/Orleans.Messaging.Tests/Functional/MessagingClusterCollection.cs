using Xunit;

namespace Orleans.Durable.Messaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MessagingClusterCollection
{
    public const string Name = "Messaging cluster";
}
