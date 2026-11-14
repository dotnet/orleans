using System.Globalization;
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

        public static IEnumerable<TheoryDataRow<string, long>> TimeSpanCases =>
        [
            new("2.5ms", 25_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_MillisecondsLower" },
            new("2.5MS", 25_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_MillisecondsUpper" },
            new("2.5Ms", 25_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_MillisecondsTitle" },
            new("2.5mS", 25_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_MillisecondsMixed" },
            new("2.5s", 25_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_SecondsLower" },
            new("2.5S", 25_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_SecondsUpper" },
            new("2.5m", 1_500_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_MinutesLower" },
            new("2.5M", 1_500_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_MinutesUpper" },
            new("2.5hr", 90_000_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_HoursLower" },
            new("2.5HR", 90_000_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_HoursUpper" },
            new("2.5Hr", 90_000_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_HoursTitle" },
            new("2.5hR", 90_000_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_HoursMixed" },
            new(" \t+1.25 mS\r\n", 12_500L) { TestDisplayName = "ParseTimeSpan_ExactTicks_Whitespace" },
            new("-0.00015MS", -1L) { TestDisplayName = "ParseTimeSpan_ExactTicks_NegativeSubTickTruncation" },
            new("2.5", 25_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_DefaultSeconds" },
            new("+1.25", 12_500_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_PositiveDefault" },
            new("-1.25", -12_500_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_NegativeDefault" },
            new("0.00000015", 1L) { TestDisplayName = "ParseTimeSpan_ExactTicks_FractionalDefaultTickTruncation" },
            new("0hr", 0L) { TestDisplayName = "ParseTimeSpan_ExactTicks_Zero" },
            new("1e2s", 1_000_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_InvariantExponent" },
            new("1,234.5s", 12_345_000_000L) { TestDisplayName = "ParseTimeSpan_ExactTicks_InvariantGrouping" },
        ];

        [Theory, MemberData(nameof(TimeSpanCases)), TestCategory("Functional"), TestCategory("Config")]
        public void ParseTimeSpan_ExactTicks(string input, long expectedTicks)
        {
            var result = ConfigUtilities.ParseTimeSpan(input, "Invalid duration");

            Assert.Equal(expectedTicks, result.Ticks);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ParseTimeSpan_UsesInvariantCultureUnderFrenchCurrentCulture()
        {
            // CurrentCulture is local to this execution context, not a process-wide default.
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

                var result = ConfigUtilities.ParseTimeSpan("1,234.5S", "Invalid duration");

                Assert.Equal(12_345_000_000L, result.Ticks);
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        public static IEnumerable<TheoryDataRow<string>> InvalidTimeSpanCases =>
        [
            new(" 1day ") { TestDisplayName = "ParseTimeSpan_InvalidInput_UnsupportedUnit" },
            new("not-a-numbermS") { TestDisplayName = "ParseTimeSpan_InvalidInput_MalformedNumber" },
            new("1\u017F") { TestDisplayName = "ParseTimeSpan_InvalidInput_UnicodeSecondSuffix" },
            new("1m\u017F") { TestDisplayName = "ParseTimeSpan_InvalidInput_UnicodeMillisecondSuffix" },
        ];

        [Theory, MemberData(nameof(InvalidTimeSpanCases)), TestCategory("Functional"), TestCategory("Config")]
        public void ParseTimeSpan_InvalidInput_PreservesCustomError(string input)
        {
            var exception = Assert.Throws<FormatException>(() => ConfigUtilities.ParseTimeSpan(input, "Custom duration error"));

            Assert.Equal("Custom duration error. Tried to parse " + input, exception.Message);
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
        public void DurationAttributes_ExposeParsedTimeSpanValuesAndPreserveCollectionMetadata()
        {
            var response = new ResponseTimeoutAttribute("00:00:42");
            var collection = new CollectionAgeLimitAttribute("00:01:15");
            var properties = new Dictionary<string, string>();

            collection.Populate(null!, null!, default, properties);

            Assert.Equal(TimeSpan.FromSeconds(42), response.Timeout);
            Assert.Equal(TimeSpan.FromSeconds(75), collection.AgeLimit);
            Assert.Equal("00:01:15", properties[WellKnownGrainTypeProperties.IdleDeactivationPeriod]);
        }

        [Theory, TestCategory("Functional"), TestCategory("Config")]
        [InlineData("")]
        [InlineData("not-a-duration")]
        public void DurationAttributes_InvalidDuration_PropagatesFormatException(string duration)
        {
            Assert.Throws<FormatException>(() => new ResponseTimeoutAttribute(duration));
            Assert.Throws<FormatException>(() => new CollectionAgeLimitAttribute(duration));
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
