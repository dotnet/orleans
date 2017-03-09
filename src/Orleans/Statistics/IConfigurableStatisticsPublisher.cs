namespace Orleans.Runtime
{
    public interface IConfigurableStatisticsPublisher : IStatisticsPublisher
    {
        void AddConfiguration(string deploymentId, bool isSilo, string siloName, SiloAddress address, System.Net.IPEndPoint gateway, string hostName);
    }
}