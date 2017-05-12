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

        public IEventStreamHandle GetEventStreamHandle(string streamName)
        {
            var grain = this.fixture.GrainFactory.GetGrain<IMemoryEventStorageGrain>(streamName.GetHashCode() % 5);

            return new EventStreamHandle(streamName, grain);
        }


        private class EventStreamHandle : IEventStreamHandle
        {

            public EventStreamHandle(string streamName, IMemoryEventStorageGrain storageGrain)
            {
                this.streamName = streamName;
                this.storageGrain = storageGrain;
            }

            private readonly string streamName;
            private readonly IMemoryEventStorageGrain storageGrain;

            public string StreamName { get { return streamName; } }

            public void Dispose() { }

            public Task<int> GetVersion()
            {
                return storageGrain.GetVersion(streamName);
            }

            public async Task<EventStreamSegment<E>> Load<E>(int startAtVersion = 0, int? endAtVersion = default(int?))
            {
                // call the grain that contains the storage
                var response = await storageGrain.Load(streamName, startAtVersion, endAtVersion);

                // must convert returned objects to type E
                return new EventStreamSegment<E>()
                {
                    StreamName = response.StreamName,
                    FromVersion = response.FromVersion,
                    ToVersion = response.ToVersion,
                    Events = response.Events.Select(kvp => new KeyValuePair<Guid, E>(kvp.Key, (E)kvp.Value)).ToList(),
                };

            }

            public Task<bool> Append<E>(IEnumerable<KeyValuePair<Guid, E>> events, int? expectedVersion = default(int?))
            {
                var eventArray = events.Select(kvp => new KeyValuePair<Guid, object>(kvp.Key, kvp.Value)).ToArray();

                // call the grain that contains the event storage
                return storageGrain.Append(streamName, eventArray, expectedVersion);
            }

            public Task<bool> Delete(int? expectedVersion = default(int?))
            {
                // call the grain that contains the event storage
                return storageGrain.Delete(streamName, expectedVersion);
            }
        }
    }
}
