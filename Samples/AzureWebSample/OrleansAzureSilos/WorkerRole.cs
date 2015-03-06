//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Azure.Silos
{
    public class WorkerRole : RoleEntryPoint
    {
        private const string DATA_CONNECTION_STRING_KEY = "DataConnectionString";
        private const string STORAGE_KEY = "Storage";
        private const string BOOTSTRAP_KEY = "Bootstrap";

        private AzureSilo orleansAzureSilo;

        public WorkerRole()
        {
            Console.WriteLine("OrleansAzureSilos-Constructor called");
        }

        public override bool OnStart()
        {
            Trace.WriteLine("OrleansAzureSilos-OnStart called", "Information");

            Trace.WriteLine("OrleansAzureSilos-OnStart Initializing config", "Information");

            // Set the maximum number of concurrent connections 
            ServicePointManager.DefaultConnectionLimit = 12;

            // For information on handling configuration changes see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.
            RoleEnvironment.Changing += RoleEnvironmentChanging;
            SetupEnvironmentChangeHandlers();

            bool ok = base.OnStart();

            Trace.WriteLine("OrleansAzureSilos-OnStart called base.OnStart ok=" + ok, "Information");

            return ok;
        }

        public override void Run()
        {
            Trace.WriteLine("OrleansAzureSilos-Run entry point called", "Information");

            Trace.WriteLine("OrleansAzureSilos-OnStart Starting Orleans silo", "Information");

            var config = new ClusterConfiguration();
            config.StandardLoad();

            // First example of how to configure an existing provider
            ConfigureExistingStorageProvider(config);
            ConfigureNewStorageProvider(config);
            ConfigureNewBootstrapProvider(config);

            // It is IMPORTANT to start the silo not in OnStart but in Run.
            // Azure may not have the firewalls open yet (on the remote silos) at the OnStart phase.
            orleansAzureSilo = new AzureSilo();
            bool ok = orleansAzureSilo.Start(RoleEnvironment.DeploymentId, RoleEnvironment.CurrentRoleInstance, config);

            Trace.WriteLine("OrleansAzureSilos-OnStart Orleans silo started ok=" + ok, "Information");

            orleansAzureSilo.Run(); // Call will block until silo is shutdown
        }

        public override void OnStop()
        {
            Trace.WriteLine("OrleansAzureSilos-OnStop called", "Information");
            if (orleansAzureSilo != null)
            {
                orleansAzureSilo.Stop();
            }
            RoleEnvironment.Changing -= RoleEnvironmentChanging;
            base.OnStop();
            Trace.WriteLine("OrleansAzureSilos-OnStop finished", "Information");
        }

        private static void RoleEnvironmentChanging(object sender, RoleEnvironmentChangingEventArgs e)
        {
            int i = 1;
            foreach (var c in e.Changes)
            {
                Trace.WriteLine(string.Format("RoleEnvironmentChanging: #{0} Type={1} Change={2}", i++, c.GetType().FullName, c));
            }

            // If a configuration setting is changing);
            if (e.Changes.Any((RoleEnvironmentChange change) => change is RoleEnvironmentConfigurationSettingChange))
            {
                // Set e.Cancel to true to restart this role instance
                e.Cancel = true;
            }
        }

        private static void SetupEnvironmentChangeHandlers()
        {
            // For information on handling configuration changes see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.

            #region Setup CloudStorageAccount Configuration Setting Publisher
            // From: http://www.tsjensen.com/blog/2009/11/30/Windows+Azure+10+CloudTableClient+Minimal+Configuration.aspx
            // This code sets up a handler to update CloudStorageAccount instances when their corresponding
            // configuration settings change in the service configuration file.
            CloudStorageAccount.SetConfigurationSettingPublisher((string configName, Func<string, bool> configSetter) =>
            {
                // Provide the configSetter with the initial value
                configSetter(RoleEnvironment.GetConfigurationSettingValue(configName));

                RoleEnvironment.Changed += (object sender, RoleEnvironmentChangedEventArgs e) =>
                {
                    if (e.Changes.OfType<RoleEnvironmentConfigurationSettingChange>()
                        .Any((RoleEnvironmentConfigurationSettingChange change) => (change.ConfigurationSettingName == configName)))
                    {
                        // The corresponding configuration setting has changed, propagate the value
                        if (!configSetter(RoleEnvironment.GetConfigurationSettingValue(configName)))
                        {
                            // In this case, the change to the storage account credentials in the
                            // service configuration is significant enough that the role needs to be
                            // recycled in order to use the latest settings. (for example, the 
                            // endpoint has changed)
                            RoleEnvironment.RequestRecycle();
                        }
                    }
                };
            });
            #endregion
        }

        // Storage Provider is already configured in the OrleansConfiguration.xml as:
        // <Provider Type="Orleans.Storage.AzureTableStorage" Name="AzureStore" DataConnectionString="UseDevelopmentStorage=true" />
        // Below is an example of how to set the storage key in the ProviderConfiguration and how to add a new custom configuration property.
        private void ConfigureExistingStorageProvider(ClusterConfiguration config)
        {
            ProviderCategoryConfiguration storageConfiguration;
            if (config.Globals.ProviderConfigurations.TryGetValue(STORAGE_KEY, out storageConfiguration))
            {
                // Modify the specific "AzureStore" provider
                IProviderConfiguration iProviderConfig = null;
                if (storageConfiguration.Providers.TryGetValue("AzureStore", out iProviderConfig))
                {
                    ProviderConfiguration storageProvider = iProviderConfig as ProviderConfiguration;
                    storageProvider.SetProperty("MyCustomProperty1", "MyCustomPropertyValue1");
                }

                // Alternatively, find all storage providers and modify them as necessary
                foreach (ProviderConfiguration providerConfig in storageConfiguration.Providers.Values.Where(provider => provider is ProviderConfiguration).Cast<ProviderConfiguration>())
                {
                    string connectionString = RoleEnvironment.GetConfigurationSettingValue(DATA_CONNECTION_STRING_KEY);
                    providerConfig.SetProperty(DATA_CONNECTION_STRING_KEY, connectionString);
                    providerConfig.SetProperty("MyCustomProperty2", "MyCustomPropertyValue2");

                }
                
                // Once silo starts you can see that it prints in the log:
                //   Providers:
                    // StorageProviders:
                        // Name=AzureStore, Type=Orleans.Storage.AzureTableStorage, Properties=[DataConnectionString, MyCustomProperty, MyCustomProperty2]
            }
        }

        // Below is an example of how to define a full configuration for a new storage provider that is not already specified in the config file.
        private void ConfigureNewStorageProvider(ClusterConfiguration config)
        {
            ProviderCategoryConfiguration categoryConfiguration;
            if (!config.Globals.ProviderConfigurations.TryGetValue(STORAGE_KEY, out categoryConfiguration))
            {
                categoryConfiguration = new ProviderCategoryConfiguration()
                {
                    Name = STORAGE_KEY,
                    Providers = new Dictionary<string, IProviderConfiguration>()
                };
                config.Globals.ProviderConfigurations.Add(STORAGE_KEY, categoryConfiguration);
            }

            string myProviderName = "MyNewAzureStoreProvider"; // what ever arbitrary name you want to give to your provider
            // Alternatively, the 2nd argument, type, can be something like typeof(EventStoreInitBootstrapProvider).FullName
            ProviderConfiguration providerConfig = new ProviderConfiguration(new Dictionary<string, string>(), "Orleans.Storage.AzureTableStorage", myProviderName);
            categoryConfiguration.Providers.Add(myProviderName, providerConfig);

            string connectionString = RoleEnvironment.GetConfigurationSettingValue(DATA_CONNECTION_STRING_KEY);
            providerConfig.SetProperty(DATA_CONNECTION_STRING_KEY, connectionString);
            providerConfig.SetProperty("MyCustomProperty3", "MyCustomPropertyValue3");

            // Once silo starts you can see that it prints in the log:
                //   Providers:
                    // StorageProviders:
                        // Name=MyNewAzureStoreProvider, Type=Orleans.Storage.AzureTableStorage, Properties=[DataConnectionString, MyCustomProperty3]
        }

        // Below is an example of how to define a full configuration for a new Bootstrap provider that is not already specified in the config file.
        private void ConfigureNewBootstrapProvider(ClusterConfiguration config)
        {
            ProviderCategoryConfiguration categoryConfiguration;
            if (!config.Globals.ProviderConfigurations.TryGetValue(BOOTSTRAP_KEY, out categoryConfiguration))
            {
                categoryConfiguration = new ProviderCategoryConfiguration()
                {
                    Name = BOOTSTRAP_KEY,
                    Providers = new Dictionary<string, IProviderConfiguration>()
                };
                config.Globals.ProviderConfigurations.Add(BOOTSTRAP_KEY, categoryConfiguration);
            }

            string myProviderName = "MyNewBootstrapProvider"; // what ever arbitrary name you want to give to your provider
            // Alternatively, the 2nd argument, type, can be something like typeof(EventStoreInitBootstrapProvider).FullName
            ProviderConfiguration providerConfig = new ProviderConfiguration(new Dictionary<string, string>(), "FullNameSpace.NewBootstrapProviderType", myProviderName);
            //categoryConfiguration.Providers.Add(myProviderName, providerConfig);

            // The last line, categoryConfiguration.Providers.Add, is commented out because the assembly with "FullNameSpace.NewBootstrapProviderType" is not added to the project,
            // this the silo will fail to load the new bootstrap provider upon startup.
            // !!!!!!!!!! Provider of type FullNameSpace.NewBootstrapProviderType name MyNewBootstrapProvider was not loaded.
            // Once you add your new provider to the project, uncommnet this line.

            // Once silo starts you can see that it prints in the log:
            //   Providers:
            // BootstrapProviders:
            // Name=MyNewBootstrapProvider, Type=FullNameSpace.NewBootstrapProviderType, Properties=[]
        }
    }
}
