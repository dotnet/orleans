using Orleans.Runtime;

namespace Orleans.MultiCluster
{
    /// <summary>
    /// Information about multicluster gateways
    /// </summary>
    public interface IMultiClusterGatewayInfo  
    {
        string ClusterId { get; }

        SiloAddress SiloAddress { get; }

        GatewayStatus Status { get;}
    }
}
