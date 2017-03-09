namespace Orleans.Runtime
{
    public interface IConfigurableClientMetricsDataPublisher : IClientMetricsDataPublisher
    {
        void AddConfiguration(string deploymentId, string hostName, string clientId, System.Net.IPAddress address);
    }
}