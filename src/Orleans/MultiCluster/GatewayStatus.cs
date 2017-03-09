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
}