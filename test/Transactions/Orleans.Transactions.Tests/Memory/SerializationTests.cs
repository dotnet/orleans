using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;
using Orleans.Runtime;
using Xunit.Abstractions;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit;

namespace Orleans.Transactions.Tests.Memory
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class SerializationTests: TransactionTestRunnerBase, IClassFixture<MemoryTransactionsFixture>
    {
        private MemoryTransactionsFixture fixture;
        public SerializationTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            :base(fixture.GrainFactory, output.WriteLine)
        {
            this.fixture = fixture;
        }

        [Fact]
        public void JsonConcertCanSerializeMetaData()
        {
            ITransactionTestGrain testGrain = this.RandomTestGrain(TransactionTestConstants.SingleStateTransactionalGrain);
            GrainReference reference = (GrainReference)testGrain;
            var metaData = new TransactionalStateMetaData();
            metaData.TimeStamp = DateTime.UtcNow;
            metaData.CommitRecords.Add(Guid.NewGuid(), new CommitRecord()
            {
                Timestamp = DateTime.UtcNow,
                WriteParticipants = new List<ParticipantId>() { new ParticipantId("bob", reference, ParticipantId.Role.Resource | ParticipantId.Role.Manager) }
            });
            JsonSerializerSettings serializerSettings = TransactionalStateFactory.GetJsonSerializerSettings(this.fixture.Client.ServiceProvider);

            //should be able to serialize it
            string jsonMetaData = JsonConvert.SerializeObject(metaData, serializerSettings);

            TransactionalStateMetaData deseriliazedMetaData = JsonConvert.DeserializeObject<TransactionalStateMetaData>(jsonMetaData, serializerSettings);
            Assert.Equal(metaData.TimeStamp, deseriliazedMetaData.TimeStamp);
        }
    }
}
