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
using TestExtensions;

namespace Orleans.Transactions.Tests.Memory
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class SerializationTests
    {
        private SerializationTestEnvironment environment;
        public SerializationTests()
        {
            var config = new ClientConfiguration { SerializationProviders = { typeof(OrleansJsonSerializer).GetTypeInfo() } };
            this.environment = SerializationTestEnvironment.InitializeWithDefaults(config);
        }

        [Fact]
        public void JsonConcertCanSerializeMetaData()
        {
            var metaData = new MetaData();
            metaData.TimeStamp = DateTime.UtcNow;
            metaData.CommitRecords = new Dictionary<Guid, CommitRecord>();
            metaData.CommitRecords.Add(Guid.NewGuid(), new CommitRecord()
            {
                Timestamp = DateTime.UtcNow,
                WriteParticipants = new List<ITransactionParticipant>() { new TransactionParticipantExtension().AsTransactionParticipant("resourceId")}
            });
            var serializerSettings = TransactionParticipantExtensionExtensions.GetJsonSerializerSettings(
                this.environment.Client.ServiceProvider.GetService<ITypeResolver>(),
                this.environment.GrainFactory);
            //should be able to serialize it
            var jsonMetaData = JsonConvert.SerializeObject(metaData, serializerSettings);

            var deseriliazedMetaData = JsonConvert.DeserializeObject<MetaData>(jsonMetaData, serializerSettings);
            Assert.Equal(metaData.TimeStamp, deseriliazedMetaData.TimeStamp);
        }
    }
}
