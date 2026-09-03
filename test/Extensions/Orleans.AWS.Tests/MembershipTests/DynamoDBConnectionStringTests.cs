using System.Globalization;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Xunit;

namespace AWSUtils.Tests.MembershipTests
{
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [TestSuite("BVT")]
    [TestProvider("DynamoDB")]
    [TestArea("Membership")]
    public class DynamoDBConnectionStringTests
    {
        [Fact]
        public void MembershipOptions_ParseNumericValuesUsingInvariantCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CreateCultureWithNonInvariantPositiveSign();
                var options = new DynamoDBClusteringOptions();

                DynamoDBMembershipHelper.ParseDataConnectionString(
                    "ReadCapacityUnits=+12;WriteCapacityUnits=+34",
                    options);

                Assert.Equal(12, options.ReadCapacityUnits);
                Assert.Equal(34, options.WriteCapacityUnits);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Fact]
        public void GatewayOptions_ParseNumericValuesUsingInvariantCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CreateCultureWithNonInvariantPositiveSign();
                var options = new DynamoDBGatewayOptions();

                DynamoDBGatewayListProviderHelper.ParseDataConnectionString(
                    "ReadCapacityUnits=+12;WriteCapacityUnits=+34",
                    options);

                Assert.Equal(12, options.ReadCapacityUnits);
                Assert.Equal(34, options.WriteCapacityUnits);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        private static CultureInfo CreateCultureWithNonInvariantPositiveSign()
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
            culture.NumberFormat.PositiveSign = "!";
            return culture;
        }
    }
}
