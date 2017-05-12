using EventSourcing.Tests;
using TestExtensions;
using Xunit;

namespace EventSourcing.Tests
{

    [CollectionDefinition("EventSourcingCluster")]
    public class EventSourcingClusterTestCollection : ICollectionFixture<EventSourcingClusterFixture> { }

}