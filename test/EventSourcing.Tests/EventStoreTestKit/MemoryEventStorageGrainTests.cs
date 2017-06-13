using Orleans;
using Orleans.Providers;
using Orleans.Runtime.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Storage;
using Orleans.Runtime.Configuration;
using Xunit;
using Orleans.Runtime;

namespace EventSourcing.Tests
{
    [Collection("EventSourcingCluster")]
    public class MemoryEventStorageGrainTests : EventStoreTests, IEventStorage 
    {
        public MemoryEventStorageGrainTests(EventSourcingClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        EventSourcingClusterFixture fixture;

        protected override IEventStorage StoreUnderTest
        {
            get
            {
                return this;
            }
        }

        public IEventStreamHandle<TEvent> GetEventStreamHandle<TEvent>(string streamName)
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMemoryEventStorageGrain<TEvent>>(streamName.GetHashCode() % 5);

            return new EventStreamHandle<TEvent>(streamName, grain);
        }


        private class EventStreamHandle<TEvent> : IEventStreamHandle<TEvent>
        {

            public EventStreamHandle(string streamName, IMemoryEventStorageGrain<TEvent> storageGrain)
            {
                this.streamName = streamName;
                this.storageGrain = storageGrain;
            }

            private readonly string streamName;
            private readonly IMemoryEventStorageGrain<TEvent> storageGrain;

            public string StreamName { get { return streamName; } }

            public void Dispose() { }

            public Task<int> GetVersion()
            {
                return storageGrain.GetVersion(streamName);
            }

            public Task<EventStreamSegment<TEvent>> Load(int startAtVersion = 0, int? endAtVersion = default(int?))
            {
                // call the grain that contains the storage
                return storageGrain.Load(streamName, startAtVersion, endAtVersion);
            }

            public Task<bool> Append(IEnumerable<KeyValuePair<Guid, TEvent>> events, int? expectedVersion = default(int?))
            {
                // call the grain that contains the event storage
                return storageGrain.Append(streamName, events, expectedVersion);
            }

            public Task<bool> Delete(int? expectedVersion = default(int?))
            {
                // call the grain that contains the event storage
                return storageGrain.Delete(streamName, expectedVersion);
            }
        }
    }
}
