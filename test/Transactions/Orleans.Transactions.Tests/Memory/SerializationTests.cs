using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Providers;
using Xunit;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Abstractions.Extensions;
using Orleans.Transactions.Tests.Correctness;
using TestExtensions;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests.Memory
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class SerializationTests: TransactionTestRunnerBase, IClassFixture<MemoryTransactionsFixture>
    {
        private MemoryTransactionsFixture fixture;
        public SerializationTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            :base(fixture.GrainFactory, output)
        {
            this.fixture = fixture;
        }

        [Fact]
        public void JsonConcertCanSerializeMetaData()
        {
            var grainRef = this.RandomTestGrain(TransactionTestConstants.SingleStateTransactionalGrain);
            var ext = grainRef.Cast<ITransactionParticipantExtension>();
            var metaData = new MetaData();
            metaData.TimeStamp = DateTime.UtcNow;
            metaData.CommitRecords = new Dictionary<Guid, CommitRecord>();
            metaData.CommitRecords.Add(Guid.NewGuid(), new CommitRecord()
            {
                Timestamp = DateTime.UtcNow,
                WriteParticipants = new List<ITransactionParticipant>() { ext.AsTransactionParticipant("resourceId")}
            });
            var serializerSettings = TransactionParticipantExtensionExtensions.GetJsonSerializerSettings(
                this.fixture.Client.ServiceProvider.GetService<ITypeResolver>(),
                this.grainFactory);
            //should be able to serialize it
            var jsonMetaData = JsonConvert.SerializeObject(metaData, serializerSettings);

            var deseriliazedMetaData = JsonConvert.DeserializeObject<MetaData>(jsonMetaData, serializerSettings);
            Assert.Equal(metaData.TimeStamp, deseriliazedMetaData.TimeStamp);
        }
    }
}
