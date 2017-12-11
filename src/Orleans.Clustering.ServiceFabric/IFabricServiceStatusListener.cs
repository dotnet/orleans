using Orleans.ServiceFabric;

namespace Orleans.Clustering.ServiceFabric
{
    /// <summary>
    /// Listener for partition changes.
    /// </summary>
    internal interface IFabricServiceStatusListener
    {
        /// <summary>
        /// Notifies this instance of an update to cluster members.
        /// </summary>
        /// <param name="silos">The updated set of silos.</param>
        void OnUpdate(FabricSiloInfo[] silos);
    }
}