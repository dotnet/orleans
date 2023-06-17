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

        /// <summary>
        /// The name of the <see cref="Orleans.Configuration.ClusterOptions.ServiceId"/> label on the pod.
        /// </summary>
        public const string ServiceIdLabel = "orleans/serviceId";

        /// <summary>
        /// The name of the <see cref="Orleans.Configuration.ClusterOptions.ClusterId"/> label on the pod.
        /// </summary>
        public const string ClusterIdLabel = "orleans/clusterId";

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
        /// The number of silos in the cluster which should monitor Kubernetes.
        /// </summary>
        /// <remarks>
        /// Setting this to a small number can reduce the load on the Kubernetes API server.
        /// </remarks>
        public int MaxAgents { get; set; } = 2;

        /// <summary>
        /// Gets or sets the maximum number of attempts to retry Kubernetes API calls.
        /// </summary>
        public int MaxKubernetesApiRetryAttempts { get; set; } = 10;

        /// <summary>
        /// Whether or not to delete pods which correspond to silos which have become defunct since this silo became active.
        /// </summary>
        public bool DeleteDefunctSiloPods { get; set; } = false;

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
