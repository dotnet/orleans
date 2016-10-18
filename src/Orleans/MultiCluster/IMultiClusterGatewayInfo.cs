using Orleans.Runtime;

namespace Orleans.MultiCluster
{
    /// <summary>
    /// Multicluster Gateways are either active (silo is a gateway), 
    /// or Inactive (silo is not a gateway)
    /// </summary>
    public enum GatewayStatus
    {
        None,
        Active,
        Inactive
    }

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
