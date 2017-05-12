using EventSourcing.Tests;
using Orleans.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace GetEventStore.Tests
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestCategory("Functional")]
    public class SmallPageSize : EventStoreTests, IClassFixture<ProvidersFixture>
    {
        private readonly ProvidersFixture fixture;

        public SmallPageSize(ITestOutputHelper output, ProvidersFixture fixture)
        {
            this.fixture = fixture;
        }

        protected override IEventStorage StoreUnderTest
        {
            get
            {
                return fixture.EventStoreSmallPageSize;
            }
        }

        // all the actual functional tests are inherited from EventStoreTests
        // they require an event store to be running on the local machine, with default configuration parameters



    }
}
