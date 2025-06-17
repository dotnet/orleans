using Orleans.Runtime.Configuration;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable CheckNamespace
// ReSharper disable ConvertToConstant.Local

namespace UnitTests
{
    /// <summary>
    /// Tests for Orleans configuration utilities, particularly the security features for handling sensitive connection strings.
    /// These tests verify that Orleans properly redacts sensitive information from configuration strings for logging and diagnostics.
    /// </summary>
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
    }
}

// ReSharper restore ConvertToConstant.Local
// ReSharper restore RedundantTypeArgumentsOfMethod
// ReSharper restore CheckNamespace