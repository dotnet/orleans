namespace Microsoft.Orleans.ServiceFabric
{
    using System;
    using System.Fabric;
    using System.IO;
    using System.Reflection;

    using global::Orleans;

    using global::Orleans.Runtime.Configuration;

    /// <summary>
    ///     Orleans client for silos hosted in Windows Fabric.
    /// </summary>
    public static class OrleansFabricClient
    {
        /// <summary>
        /// Initializes the silo client.
        /// </summary>
        /// <param name="serviceName">
        /// The Windows Fabric service name.
        /// </param>
        /// <param name="config">
        /// The configuration, or <see langword="null"/> to load from the current directory.
        /// </param>
        public static void Initialize(Uri serviceName, ClientConfiguration config = null)
        {
            var deploymentId = OrleansFabricUtility.GetDeploymentId(serviceName);
            if (config == null)
            {
                config = new ClientConfiguration();
                using (var reader = File.OpenText(Path.Combine(GetAssemblyPath(), "ClientConfiguration.xml")))
                {
                    config.Load(reader);
                }
            }

            config.DeploymentId = deploymentId;
            GrainClient.Initialize(config);
        }

        /// <summary>
        /// Initializes the silo client.
        /// </summary>
        /// <param name="serviceName">
        /// The Windows Fabric service name.
        /// </param>
        /// <param name="partition">
        /// The partition, or <see langword="null"/> for a singleton partition.
        /// </param>
        /// <param name="config">
        /// The configuration, or <see langword="null"/> to load from the current directory.
        /// </param>
        public static void Initialize(
            Uri serviceName, 
            ServicePartitionInformation partition, 
            ClientConfiguration config = null)
        {
            var deploymentId = OrleansFabricUtility.GetDeploymentId(serviceName);
            if (config == null)
            {
                config = new ClientConfiguration();
                using (var reader = File.OpenText(Path.Combine(GetAssemblyPath(), "ClientConfiguration.xml")))
                {
                    config.Load(reader);
                }
            }

            config.DeploymentId = deploymentId;
            GrainClient.Initialize(config);
        }

        /// <summary>
        ///     Returns the path of the executing assembly.
        /// </summary>
        /// <returns>
        ///     The path of the executing assembly.
        /// </returns>
        private static string GetAssemblyPath()
        {
            return Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
        }
    }
}