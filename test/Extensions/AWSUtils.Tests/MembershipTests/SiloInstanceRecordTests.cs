using System.Net;
using Amazon.DynamoDBv2.Model;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace AWSUtils.Tests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    public class SiloInstanceRecordTests
    {
        [Fact]
        public void GetKeysTest()
        {
            SiloAddress address = SiloAddress.New(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 12345), 67890); 
            var instanceRecord = new SiloInstanceRecord
            {
                DeploymentId = "deploymentID",
                SiloIdentity = SiloInstanceRecord.ConstructSiloIdentity(address)
            };

            Dictionary<string, AttributeValue> keys = instanceRecord.GetKeys();

            Assert.Equal(2, keys.Count);
            Assert.Equal(instanceRecord.DeploymentId, keys[SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME].S);
            Assert.Equal(instanceRecord.SiloIdentity, keys[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S);
        }
    }
}