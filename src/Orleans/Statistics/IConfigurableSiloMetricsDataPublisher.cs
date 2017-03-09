namespace Orleans.Runtime
{
    public interface IConfigurableSiloMetricsDataPublisher : ISiloMetricsDataPublisher
    {
        void AddConfiguration(string deploymentId, bool isSilo, string siloName, SiloAddress address, System.Net.IPEndPoint gateway, string hostName);
    }
}