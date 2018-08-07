using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;
using Orleans.Runtime;
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
            ITransactionTestGrain testGrain = this.RandomTestGrain(TransactionTestConstants.SingleStateTransactionalGrain);
            GrainReference reference = (GrainReference)testGrain;
            var metaData = new MetaData();
            metaData.TimeStamp = DateTime.UtcNow;
            metaData.CommitRecords = new Dictionary<Guid, CommitRecord>();
            metaData.CommitRecords.Add(Guid.NewGuid(), new CommitRecord()
            {
                Timestamp = DateTime.UtcNow,
                WriteParticipants = new List<ParticipantId>() { new ParticipantId("bob", reference) }
            });
            JsonSerializerSettings serializerSettings = TransactionalStateFactory.GetJsonSerializerSettings(
                this.fixture.Client.ServiceProvider.GetService<ITypeResolver>(),
                this.grainFactory);
            //should be able to serialize it
            string jsonMetaData = JsonConvert.SerializeObject(metaData, serializerSettings);

            MetaData deseriliazedMetaData = JsonConvert.DeserializeObject<MetaData>(jsonMetaData, serializerSettings);
            Assert.Equal(metaData.TimeStamp, deseriliazedMetaData.TimeStamp);
        }
    }
}
