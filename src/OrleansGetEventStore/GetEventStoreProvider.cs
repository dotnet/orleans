using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Exceptions;
using EventStore.ClientAPI.SystemData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;
using Orleans.LogConsistency;
using System.Threading;
using Orleans.Providers;
using Orleans.Storage;
using Orleans.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.EventSourcing
{
    /// <summary>
    /// A log view provider that stores the log in geteventstore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Configuration parameters:
    /// <c>ConnectionString</c> -- must be a URI supported by GetEventStore. Defaults to <c>tcp://admin:changeit@localhost:1113</c>.
    /// <c>PageSize</c> -- number of events batched together during writing. Defaults to <c>500</c>c>.
    /// <c>StoreNecessaryTypenamesOnly</c> -- whether to skip implied CLR typenames. Defaults to <c>true</c>.
    /// <c>UseFullAssemblyNames</c> -- whether to use fully qualified assembly names. Defaults to <c>false</c>.
    /// <c>IndentJSON</c> -- whether to indent the JSON. Defaults to <c>false</c>.
    /// </para>
    /// </remarks>
    public class GetEventStoreProvider : IEventStorageProvider
    {
        private JsonSerializerSettings jsonSettings;
        private IEventStoreConnection connection;

        private string serviceId;

        private readonly int id;
        private static int counter;

        private int pageSize;

        /// <summary> Name of this event-storage provider instance. </summary>
        /// <see cref="IProvider.Name"/>
        public string Name { get; private set; }

        /// <summary> Logger used by this event-storage provider instance. </summary>
        /// <see cref="IStorageProvider.Log"/>
        public Logger Log { get; private set; }

        public GetEventStoreProvider()
        {
            id = Interlocked.Increment(ref counter);
        }

        public const string ConnectionStringParameterName = "ConnectionString";
        public const string PageSizeParameterName = "PageSize";
        public const string StoreAllTypenamesParameterName = "StoreAllTypenames";
        public const string StoreObjectIdentityParameterName = "StoreObjectIdentity";

        public string DefaultStreamName(Type grainType, GrainReference grainReference)
        {
            return string.Format("{0}_{1}", serviceId, grainReference.ToKeyString());
        }

        // default server configuration used by GetEventStore, we match them for easy testing on a local machine
        public const string GetEventStoreDefaultConnectionString = "tcp://admin:changeit@localhost:1113";

        private static readonly byte[] emptyMetaData = new byte[0];

        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            serviceId = providerRuntime.ServiceId.ToString();

            this.Name = name;
            this.Log = providerRuntime.GetLogger(string.Format("LogViews.{0}.{1}", GetType().Name, id));

            pageSize = config.GetIntProperty(PageSizeParameterName, 500);
            var storeAllTypenames = config.GetBoolProperty(StoreAllTypenamesParameterName, false);
            var storeObjectIdentity = config.GetBoolProperty(StoreObjectIdentityParameterName, false);

            var connectionString = config.GetProperty(ConnectionStringParameterName, GetEventStoreDefaultConnectionString);
            Log.Info($"Init Name={Name} ConnectionString={connectionString} PageSize={pageSize} StoreAllTypenames={storeAllTypenames} StoreObjectIdentity={storeObjectIdentity} Severity={Log.SeverityLevel}");

            var serializationManager = providerRuntime.ServiceProvider.GetService<SerializationManager>();
            jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(serializationManager, providerRuntime.GrainFactory), config);
            jsonSettings.TypeNameHandling = storeAllTypenames ? TypeNameHandling.All : TypeNameHandling.Auto;
            jsonSettings.PreserveReferencesHandling = storeObjectIdentity ? PreserveReferencesHandling.Objects : PreserveReferencesHandling.None;

            // connect to the GetEventStore service
            var uri = new Uri(connectionString);
            var settings = ConnectionSettings.Create().KeepReconnecting().KeepRetrying();
            this.connection = EventStoreConnection.Create(settings, uri);
            await this.connection.ConnectAsync().ConfigureAwait(false);

            Log.Verbose("Successfully Connected");
        }

        public Task Close()
        {
            this.connection.Close();
            return TaskDone.Done;
        }


        public async Task<bool> Append<E>(string streamName, IEnumerable<KeyValuePair<Guid,E>> events, int? expectedVersion)
        {
            var eventsToSave = events.Select(e => SerializeEvent<E>(e.Key, e.Value)).ToArray();

            if (eventsToSave.Length == 0)
                return true;

            var version = expectedVersion.HasValue
                ? (expectedVersion.Value == 0 ? ExpectedVersion.NoStream : expectedVersion.Value - 1)
                : ExpectedVersion.Any;

            if (eventsToSave.Length < pageSize)
            {
                try
                {
                    await this.connection.AppendToStreamAsync(streamName, version, eventsToSave).ConfigureAwait(false);
                }
                catch (WrongExpectedVersionException)
                {
                    return false;
                }
            }
            else
            {
                try
                {
                    var transaction = await this.connection.StartTransactionAsync(streamName, version).ConfigureAwait(false);

                    var position = 0;
                    while (position < eventsToSave.Length)
                    {
                        var count = Math.Min(pageSize, eventsToSave.Length - position);
                        var pageEvents = eventsToSave.Subrange(position, count);
                        await transaction.WriteAsync(pageEvents);
                        position += pageSize;
                    }

                    await transaction.CommitAsync();
                }
                catch (WrongExpectedVersionException)
                {
                    return false;
                }
            }

            return true;
        }

        public async Task<int> GetVersion(string streamName)
        {
            // read the latest event
            var response = await connection.ReadEventAsync(streamName, StreamPosition.End, true).ConfigureAwait(false);

            if (response.Status == EventReadStatus.NotFound
                || response.Status == EventReadStatus.NoStream
                || response.Status == EventReadStatus.StreamDeleted)
            {
                return 0;
            }

            return (int) response.Event.Value.Event.EventNumber + 1;
        }

        public async Task<EventStreamSegment<E>> Load<E>(string streamName, int fromVersion, int? toVersion)
        {
            // check for invalid range parameters
            if (fromVersion < 0)
            {
                throw new ArgumentException("invalid range", nameof(fromVersion));
            }
            if (toVersion.HasValue && toVersion.Value < fromVersion)
            {
                throw new ArgumentException("invalid range", nameof(toVersion));
            }

            // special case: requested segment is empty
            // The spec says that the returned segment must always be a valid subrange of the log.
            // There is nothing wrong with asking for an empty subrange... 
            // ... but we still need to make sure it is a valid subrange, i.e.not outside the range of the log.
            if (toVersion == fromVersion)
            {
                // return an empty segment
                // that is within the current range of the stream, i.e. not larger than latest version
                var versionThatExists = Math.Min(fromVersion, await GetVersion(streamName).ConfigureAwait(false));
                return new EventStreamSegment<E>()
                {
                    StreamName = streamName,
                    FromVersion = versionThatExists,
                    ToVersion = versionThatExists,
                    Events = new List<KeyValuePair<Guid,E>>()
                };
            }

            // general case: load segment in batches
            var sliceStart = fromVersion;
            StreamEventsSlice currentSlice = null;
            var events = new List<KeyValuePair<Guid,E>>();
            do
            {
                var readPageSize = Math.Min(pageSize, (toVersion ?? int.MaxValue) - sliceStart);

                if (readPageSize == 0)
                {
                    break;
                }

                currentSlice = await this.connection.ReadStreamEventsForwardAsync(streamName, sliceStart, readPageSize, true).ConfigureAwait(false);

                if (currentSlice.Status == SliceReadStatus.StreamNotFound
                    || currentSlice.Status == SliceReadStatus.StreamDeleted)

                    // stream does not exist... return an empty segment
                    return new EventStreamSegment<E>()
                    {
                        StreamName = streamName,
                        FromVersion = 0,
                        ToVersion = 0,
                        Events = new List<KeyValuePair<Guid, E>>()
                    };

                if (fromVersion > currentSlice.LastEventNumber)
                {
                    // the fromVersion is larger than last event in the slice; 
                    // this means the query targeted a non-existing segment. Return empty segment.
                    return new EventStreamSegment<E>()
                    {
                        StreamName = streamName,
                        FromVersion = (int) currentSlice.LastEventNumber + 1,
                        ToVersion = (int) currentSlice.LastEventNumber + 1,
                        Events = new List<KeyValuePair<Guid, E>>()
                    };
                }

                foreach (var evnt in currentSlice.Events)
                {
                    var cast = DeserializeEvent<E>(evnt.Event);
                    events.Add(new KeyValuePair<Guid, E>(evnt.Event.EventId, DeserializeEvent<E>(evnt.Event)));
                }

                sliceStart = (int) currentSlice.NextEventNumber;

            } while (!currentSlice.IsEndOfStream);

            return new EventStreamSegment<E>()
            {
                StreamName = streamName,
                FromVersion = fromVersion,
                ToVersion = fromVersion + events.Count,
                Events = events
            };
        }

        public async Task<bool> Delete(string streamName, int? expectedVersion)
        {
            try
            {
                var versionParameter = expectedVersion.HasValue ? expectedVersion.Value - 1 : ExpectedVersion.Any;
                var result = await this.connection.DeleteStreamAsync(streamName, versionParameter, true).ConfigureAwait(false);
                return true;
            }
            catch (WrongExpectedVersionException)
            {
                return false;
            }
            catch (StreamDeletedException)
            {
                return !expectedVersion.HasValue || expectedVersion.Value == 0;
            }
        }

        public E DeserializeEvent<E>(RecordedEvent @event)
        {
            return JsonConvert.DeserializeObject<E>(Encoding.UTF8.GetString(@event.Data), jsonSettings);
        }

        private EventData SerializeEvent<E>(Guid eventId, E evnt)
        {
            var jsonData = JsonConvert.SerializeObject(evnt, typeof(E), jsonSettings);

            // this typename is not used for deserialization, so there is no need to fully qualify it.
            var friendlyname = evnt.GetType().Name;

            return new EventData(eventId, friendlyname, true, Encoding.UTF8.GetBytes(jsonData), emptyMetaData);
        }

        public IEventStreamHandle GetEventStreamHandle(string streamName)
        {
            return new EventStreamHandle(streamName, this);
        }

        private class EventStreamHandle : IEventStreamHandle
        {
            public EventStreamHandle(string streamName, GetEventStoreProvider provider)
            {
                this.streamName = streamName;
                this.provider = provider;
            }

            private string streamName;
            private GetEventStoreProvider provider;

            public string StreamName { get { return streamName; } }

            public Task<bool> Append<E>(IEnumerable<KeyValuePair<Guid, E>> events, int? expectedVersion = default(int?))
            {
                return provider.Append<E>(streamName, events, expectedVersion);
            }
            public Task<bool> Delete(int? expectedVersion = default(int?))
            {
                return provider.Delete(streamName, expectedVersion);
            }
            public void Dispose()
            {
            }
            public Task<int> GetVersion()
            {
                return provider.GetVersion(streamName);
            }
            public Task<EventStreamSegment<E>> Load<E>(int startAtVersion = 0, int? endAtVersion = default(int?))
            {
                return provider.Load<E>(streamName, startAtVersion, endAtVersion);
            }
        }


        public ILogViewAdaptor<TLogView, TLogEntry> MakeLogViewAdaptor<TLogView, TLogEntry>(ILogViewAdaptorHost<TLogView, TLogEntry> hostGrain, TLogView initialState, string grainTypeName, IStorageProvider storageProvider, IEventStorageProvider eventStorageProvider, ILogConsistencyProtocolServices services)
            where TLogView : class, new()
            where TLogEntry : class
        {
            throw new NotImplementedException();
        }
    }
}