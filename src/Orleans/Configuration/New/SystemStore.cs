using System;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration.New
{
    public class SystemStore
    {
        public SystemStore()
        {
            DeploymentId = Environment.UserName;
            DataConnectionString = "";
            // Assume the ado invariant is for sql server storage if not explicitly specified
            AdoInvariant = Constants.INVARIANT_NAME_SQL_SERVER;
        }

        public ClientConfiguration.GatewayProviderType? SystemStoreType { get; set; }
        public string CustomGatewayProviderAssemblyName { get; set; }

        /// <summary>
        /// Specifies a unique identifier of this deployment.
        /// If the silos are deployed on Azure (run as workers roles), deployment id is set automatically by Azure runtime, 
        /// accessible to the role via RoleEnvironment.DeploymentId static variable and is passed to the silo automatically by the role via config. 
        /// So if the silos are run as Azure roles this variable should not be specified in the OrleansConfiguration.xml (it will be overwritten if specified).
        /// If the silos are deployed on the cluster and not as Azure roles, this variable should be set by a deployment script in the OrleansConfiguration.xml file.
        /// </summary>
        public string DeploymentId { get; set; }
        /// <summary>
        /// Specifies the connection string for the gateway provider.
        /// If the silos are deployed on Azure (run as workers roles), DataConnectionString may be specified via RoleEnvironment.GetConfigurationSettingValue("DataConnectionString");
        /// In such a case it is taken from there and passed to the silo automatically by the role via config.
        /// So if the silos are run as Azure roles and this config is specified via RoleEnvironment, 
        /// this variable should not be specified in the OrleansConfiguration.xml (it will be overwritten if specified).
        /// If the silos are deployed on the cluster and not as Azure roles,  this variable should be set in the OrleansConfiguration.xml file.
        /// If not set at all, DevelopmentStorageAccount will be used.
        /// </summary>
        public string DataConnectionString { get; set; }

        /// <summary>
        /// When using ADO, identifies the underlying data provider for the gateway provider. This three-part naming syntax is also used when creating a new factory 
        /// and for identifying the provider in an application configuration file so that the provider name, along with its associated 
        /// connection string, can be retrieved at run time. https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx
        /// </summary>
        public string AdoInvariant { get; set; }
    }
}