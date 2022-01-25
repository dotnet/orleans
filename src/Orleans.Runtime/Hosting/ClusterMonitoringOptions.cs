namespace Orleans.Hosting
{
    public sealed class ClusterMonitoringOptions
    {
        /// <summary>
        /// The number of silos in the cluster which should monitor Kubernetes.
        /// </summary>
        /// <remarks>
        /// Setting this to a small number can reduce the load on the Kubernetes API server.
        /// </remarks>
        public int MaxAgents { get; set; } = 2;

        /// <summary>
        /// Whether or not to delete pods which correspond to silos which have become defunct since this silo became active.
        /// </summary>
        public bool DeleteDefunctSiloPods { get; set; } = false;
    }
}
