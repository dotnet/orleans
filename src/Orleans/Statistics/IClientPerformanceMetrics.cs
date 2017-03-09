namespace Orleans.Runtime
{
    /// <summary>
    /// A small set of per-Orleans-client important key performance metrics.
    /// </summary>
    public interface IClientPerformanceMetrics : ICorePerformanceMetrics
    {
        /// <summary>
        /// number of gateways that this client is currently connected to.
        /// </summary>
        long ConnectedGatewayCount { get; }
    }
}