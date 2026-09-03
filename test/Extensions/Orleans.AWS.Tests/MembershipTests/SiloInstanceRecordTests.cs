using System.Globalization;
using System.Net;
using Amazon.DynamoDBv2.Model;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace AWSUtils.Tests.MembershipTests
{
    /// <summary>
    /// Tests DynamoDB silo instance record key generation and retrieval for membership table entries.
    /// </summary>
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [TestSuite("Functional")]
    [TestProvider("DynamoDB")]
    [TestArea("Membership")]
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

        [Fact]
        public void NumericFields_RoundTripUsingInvariantCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CreateCultureWithNonInvariantNegativeSign();
                var instanceRecord = new SiloInstanceRecord
                {
                    DeploymentId = "deploymentID",
                    SiloIdentity = "siloIdentity",
                    Port = -12345,
                    Generation = -67890,
                    Status = -1,
                    ProxyPort = -23456,
                    MembershipVersion = -2,
                    ETag = -3
                };

                var fields = instanceRecord.GetFields();

                Assert.Equal("-12345", fields[SiloInstanceRecord.PORT_PROPERTY_NAME].N);
                Assert.Equal("-67890", fields[SiloInstanceRecord.GENERATION_PROPERTY_NAME].N);
                Assert.Equal("-1", fields[SiloInstanceRecord.STATUS_PROPERTY_NAME].N);
                Assert.Equal("-23456", fields[SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME].N);
                Assert.Equal("-2", fields[SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME].N);
                Assert.Equal("-3", fields[SiloInstanceRecord.ETAG_PROPERTY_NAME].N);

                var roundTripped = new SiloInstanceRecord(fields);

                Assert.Equal(instanceRecord.Port, roundTripped.Port);
                Assert.Equal(instanceRecord.Generation, roundTripped.Generation);
                Assert.Equal(instanceRecord.Status, roundTripped.Status);
                Assert.Equal(instanceRecord.ProxyPort, roundTripped.ProxyPort);
                Assert.Equal(instanceRecord.MembershipVersion, roundTripped.MembershipVersion);
                Assert.Equal(instanceRecord.ETag, roundTripped.ETag);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Fact]
        public void SiloIdentity_RoundTripsUsingInvariantCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CreateCultureWithNonInvariantNegativeSign();
                var address = SiloAddress.New(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 12345), 67890);

                var identity = SiloInstanceRecord.ConstructSiloIdentity(address);

                Assert.Equal("127.0.0.1-12345-67890", identity);
                Assert.Equal(address, SiloInstanceRecord.UnpackRowKey(identity));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        private static CultureInfo CreateCultureWithNonInvariantNegativeSign()
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
            culture.NumberFormat.NegativeSign = "~";
            return culture;
        }
    }
}
