using k8s;
using System;

namespace Orleans.Hosting.Kubernetes
{
    /// <summary>
    /// Options for hosting in Kubernetes.
    /// </summary>
    public sealed class KubernetesHostingOptions
    {
        private readonly Lazy<KubernetesClientConfiguration> _clientConfiguration;

        /// <summary>
        /// The environment variable for specifying the Kubernetes namespace which all silos in this cluster belong to.
        /// </summary>
        public const string PodNamespaceEnvironmentVariable = "POD_NAMESPACE";

        /// <summary>
        /// The environment variable for specifying the name of the Kubernetes pod which this silo is executing in.
        /// </summary>
        public const string PodNameEnvironmentVariable = "POD_NAME";

        /// <summary>
        /// The environment variable for specifying the IP address of this pod.
        /// </summary>
        public const string PodIPEnvironmentVariable = "POD_IP";

        /// <summary>
        /// The environment variable for specifying <see cref="Orleans.Configuration.ClusterOptions.ClusterId"/>.
        /// </summary>
        public const string ClusterIdEnvironmentVariable = "ORLEANS_CLUSTER_ID";

        /// <summary>
        /// The environment variable for specifying <see cref="Orleans.Configuration.ClusterOptions.ServiceId"/>.
        /// </summary>
        public const string ServiceIdEnvironmentVariable = "ORLEANS_SERVICE_ID";

        public KubernetesHostingOptions()
        {
            _clientConfiguration = new Lazy<KubernetesClientConfiguration>(() => this.GetClientConfiguration());
        }

        /// <summary>
        /// Gets the client configuration.
        /// </summary>
        internal KubernetesClientConfiguration ClientConfiguration => _clientConfiguration.Value;

        /// <summary>
        /// The delegate used to get an instance of <see cref="KubernetesClientConfiguration"/>.
        /// </summary>
        public Func<KubernetesClientConfiguration> GetClientConfiguration { get; set; } = KubernetesClientConfiguration.InClusterConfig;

        /// <summary>
        /// The Kubernetes namespace which this silo and all other silos belong to.
        /// </summary>
        internal string Namespace { get; set; }

        /// <summary>
        /// The name of the Kubernetes pod which this silo is executing in.
        /// </summary>
        internal string PodName { get; set; }

        /// <summary>
        /// The PodIP of the Kubernetes pod which this silo is executing in.
        /// </summary>
        internal string PodIP { get; set; }
    }
}
