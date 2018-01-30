using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    public class TestClusterOptions
    {
        public string ClusterId { get; set; }
        public Guid ServiceId { get; set; }
        public int BaseSiloPort{ get; set; }
        public int BaseGatewayPort { get; set; }
        public bool UseTestClusterMembership { get; set; }
        public bool InitializeClientOnDeploy { get; set; }
        public short InitialSilosCount { get; set; }
        public string ApplicationBaseDirectory { get; set; }
        public bool ConfigureFileLogging { get; set; }
        public bool AssumeHomogenousSilosForTesting { get; set; }
        public List<string> SiloBuilderConfiguratorTypes { get; } = new List<string>();
        public List<string> ClientBuilderConfiguratorTypes { get; } = new List<string>();
        
        public Dictionary<string, string> ToDictionary()
        {
            var result = new Dictionary<string, string>
            {
                [nameof(ClusterId)] = this.ClusterId,
                [nameof(ServiceId)] = this.ServiceId.ToString(),
                [nameof(BaseSiloPort)] = this.BaseSiloPort.ToString(),
                [nameof(BaseGatewayPort)] = this.BaseGatewayPort.ToString(),
                [nameof(UseTestClusterMembership)] = this.UseTestClusterMembership.ToString(),
                [nameof(InitializeClientOnDeploy)] = this.InitializeClientOnDeploy.ToString(),
                [nameof(InitialSilosCount)] = this.InitialSilosCount.ToString(),
                [nameof(ApplicationBaseDirectory)] = this.ApplicationBaseDirectory,
                [nameof(ConfigureFileLogging)] = this.ConfigureFileLogging.ToString(),
                [nameof(AssumeHomogenousSilosForTesting)] = this.AssumeHomogenousSilosForTesting.ToString(),
            };
            
            if (this.SiloBuilderConfiguratorTypes != null)
            {
                for (int i = 0; i < this.SiloBuilderConfiguratorTypes.Count; i++)
                {
                    result[$"{nameof(SiloBuilderConfiguratorTypes)}:{i}"] = this.SiloBuilderConfiguratorTypes[i];
                }
            }

            if (this.ClientBuilderConfiguratorTypes != null)
            {
                for (int i = 0; i < this.ClientBuilderConfiguratorTypes.Count; i++)
                {
                    result[$"{nameof(ClientBuilderConfiguratorTypes)}:{i}"] = this.ClientBuilderConfiguratorTypes[i];
                }
            }

            return result;
        }
    }

    public class TestSiloSpecificOptions
    {
        public int SiloPort { get; set; }
        public int GatewayPort { get; set; }
        public string SiloName { get; set; }
        public int PrimarySiloPort { get; set; }

        public static TestSiloSpecificOptions Create(TestClusterOptions testClusterOptions, int instanceNumber)
        {
            var siloName = testClusterOptions.UseTestClusterMembership && instanceNumber == 0
                ? Silo.PrimarySiloName
                : $"Secondary_{instanceNumber}";

            var result = new TestSiloSpecificOptions
            {
                SiloPort = testClusterOptions.BaseSiloPort + instanceNumber,
                GatewayPort = testClusterOptions.BaseGatewayPort + instanceNumber,
                SiloName = siloName,
                PrimarySiloPort = testClusterOptions.UseTestClusterMembership ? testClusterOptions.BaseSiloPort : 0,
            };

            return result;
        }

        public Dictionary<string, string> ToDictionary() => new Dictionary<string, string>
        {
            [nameof(SiloPort)] = this.SiloPort.ToString(),
            [nameof(GatewayPort)] = this.GatewayPort.ToString(),
            [nameof(SiloName)] = this.SiloName,
            [nameof(PrimarySiloPort)] = this.PrimarySiloPort.ToString()
        };
    }
}
