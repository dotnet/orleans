using Orleans.EventSourcing.StateStorage;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace Tester.EventSourcingTests
{
    [CollectionDefinition(EventSourceEnvironmentFixture.EventSource)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<EventSourceEnvironmentFixture> { }

    public class EventSourceEnvironmentFixture : SerializationTestEnvironment
    {
        // Force load of OrleansEventSourcing
        private static readonly GrainStateWithMetaDataAndETag<object> dummy = new GrainStateWithMetaDataAndETag<object>();

        public const string EventSource = "EventSourceTestEnvironment";

        public T RoundTripSerialization<T>(T source)
        {
            BinaryTokenStreamWriter writer = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(source, writer);
            T output = (T)SerializationManager.Deserialize(new BinaryTokenStreamReader(writer.ToByteArray()));

            return output;
        }
    }
}