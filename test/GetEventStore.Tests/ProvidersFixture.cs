using EventSourcing.Tests;
using Orleans.EventSourcing;
using Orleans.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;

namespace GetEventStore.Tests
{
    // creates several instance of a GetEventStore provider, with various parameter choices
    public class ProvidersFixture : EventStorageProviderFixture
    {

        public readonly IEventStorageProvider EventStoreDefault;
        public readonly IEventStorageProvider EventStoreSmallPageSize;

        public readonly IEventStorageProvider EventStoreAllTypenames;

        public readonly IEventStorageProvider EventStoreObjectIdentity;


        public ProvidersFixture(TestEnvironmentFixture groupFixture) : base(groupFixture)
        {

            EventStoreDefault = new GetEventStoreProvider();
            this.InitProvider(EventStoreDefault, "StandardPageSize");

            EventStoreSmallPageSize = new GetEventStoreProvider();
            var props = new Dictionary<string, string>();
            props.Add(GetEventStoreProvider.PageSizeParameterName, "1");
            this.InitProvider(EventStoreSmallPageSize, "SmallPageSize", props);

            EventStoreAllTypenames = new GetEventStoreProvider();
            props = new Dictionary<string, string>();
            props.Add(GetEventStoreProvider.StoreAllTypenamesParameterName, "true");
            this.InitProvider(EventStoreAllTypenames, "AllTypenames", props);

            EventStoreObjectIdentity = new GetEventStoreProvider();
            props = new Dictionary<string, string>();
            props.Add(GetEventStoreProvider.StoreObjectIdentityParameterName, "true");
            this.InitProvider(EventStoreObjectIdentity, "ObjectIdentity", props);
        }
    }
}
