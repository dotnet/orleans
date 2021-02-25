using System;
using Orleans.Runtime.Configuration;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable CheckNamespace
// ReSharper disable ConvertToConstant.Local

namespace UnitTests
{
    public class ConfigTests
    {
        private readonly ITestOutputHelper output;

        public ConfigTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
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

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("AdoNet")]
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