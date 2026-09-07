using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;

// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable CheckNamespace
// ReSharper disable ConvertToConstant.Local

namespace UnitTests
{
    /// <summary>
    /// Tests for Orleans configuration utilities, particularly the security features for handling sensitive connection strings.
    /// These tests verify that Orleans properly redacts sensitive information from configuration strings for logging and diagnostics.
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class ConfigTests
    {
        private readonly ITestOutputHelper output;

        public ConfigTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Tests that Azure Storage connection strings have their account keys properly redacted.
        /// This prevents sensitive authentication information from being exposed in logs.
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_AzureConnectionInfo()
        {
            string azureConnectionStringInput =
                @"DefaultEndpointsProtocol=https;AccountName=test;AccountKey=q-SOMEKEY-==";
            output.WriteLine("Input = " + azureConnectionStringInput);
            string azureConnectionString = ConfigUtilities.RedactConnectionStringInfo(azureConnectionStringInput);
            output.WriteLine("Output = " + azureConnectionString);
            Assert.True(azureConnectionString.EndsWith("AccountKey=<--SNIP-->", StringComparison.InvariantCultureIgnoreCase),
                "Removed account key info from Azure connection string " + azureConnectionString);
        }

        /// <summary>
        /// Tests that SQL Server connection strings have their passwords properly redacted.
        /// This ensures database credentials are not exposed in logs or error messages.
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_AdoNetConnectionInfo()
        {
            string sqlConnectionStringInput =
                @"Server=myServerName\myInstanceName;Database=myDataBase;User Id=myUsername;Password=myPassword";
            output.WriteLine("Input = " + sqlConnectionStringInput);
            string sqlConnectionString = ConfigUtilities.RedactConnectionStringInfo(sqlConnectionStringInput);
            output.WriteLine("Output = " + sqlConnectionString);
            Assert.True(sqlConnectionString.EndsWith("Password=<--SNIP-->", StringComparison.InvariantCultureIgnoreCase),
                "Removed password info from SqlServer connection string " + sqlConnectionString);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_LocalIPAddressFallback_UsesIPv4Loopback()
        {
            var result = ConfigUtilities.ResolveLocalIPAddress(Array.Empty<IPAddress>(), AddressFamily.InterNetwork, interfaceName: null);
            Assert.Equal(IPAddress.Loopback, result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_LocalIPAddressFallback_UsesIPv6Loopback()
        {
            var result = ConfigUtilities.ResolveLocalIPAddress(Array.Empty<IPAddress>(), AddressFamily.InterNetworkV6, interfaceName: null);
            Assert.Equal(IPAddress.IPv6Loopback, result);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_LocalIPAddressFallback_ThrowsForExplicitInterface()
        {
            Assert.Throws<Orleans.Runtime.OrleansException>(() => ConfigUtilities.ResolveLocalIPAddress(Array.Empty<IPAddress>(), AddressFamily.InterNetwork, "en0"));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void CollectionAttributes_NullProperties_ThrowWithExactParameterName()
        {
            var collectionAgeLimit = new CollectionAgeLimitAttribute();
            var collectionException = Assert.Throws<ArgumentNullException>(
                () => collectionAgeLimit.Populate(null!, null!, default, null!));
            Assert.Equal("properties", collectionException.ParamName);

            var keepAlive = new KeepAliveAttribute();
            var keepAliveException = Assert.Throws<ArgumentNullException>(
                () => keepAlive.Populate(null!, null!, default, null!));
            Assert.Equal("properties", keepAliveException.ParamName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void CollectionAttributes_ValidProperties_PreserveIdleDeactivationValues()
        {
            var collectionProperties = new Dictionary<string, string>();
            new CollectionAgeLimitAttribute { Minutes = 5 }.Populate(null!, null!, default, collectionProperties);

            Assert.Equal(
                TimeSpan.FromMinutes(5).ToString("c"),
                collectionProperties[WellKnownGrainTypeProperties.IdleDeactivationPeriod]);

            var keepAliveProperties = new Dictionary<string, string>();
            new KeepAliveAttribute().Populate(null!, null!, default, keepAliveProperties);

            Assert.Equal(
                WellKnownGrainTypeProperties.IndefiniteIdleDeactivationPeriodValue,
                keepAliveProperties[WellKnownGrainTypeProperties.IdleDeactivationPeriod]);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClusterOptionsValidator_NullOptions_ThrowsWithExactParameterName()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => new ClusterOptionsValidator(null!));

            Assert.Equal("options", exception.ParamName);
        }
    }
}

// ReSharper restore ConvertToConstant.Local
// ReSharper restore RedundantTypeArgumentsOfMethod
// ReSharper restore CheckNamespace
