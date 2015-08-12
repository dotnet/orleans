/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Azure.Silos
{
    public class WorkerRole : RoleEntryPoint
    {
        private const string DATA_CONNECTION_STRING_KEY = "DataConnectionString";

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
            Example_ConfigureExistingStorageProvider(config);
            Example_ConfigureNewStorageProvider(config);
            Example_ConfigureNewBootstrapProvider(config);

            // It is IMPORTANT to start the silo not in OnStart but in Run.
            // Azure may not have the firewalls open yet (on the remote silos) at the OnStart phase.
            orleansAzureSilo = new AzureSilo();
            bool ok = orleansAzureSilo.Start(config);

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
        }

        // Storage Provider is already configured in the OrleansConfiguration.xml as:
        // <Provider Type="Orleans.Storage.AzureTableStorage" Name="AzureStore" DataConnectionString="UseDevelopmentStorage=true" />
        // Below is an example of how to set the storage key in the ProviderConfiguration and how to add a new custom configuration property.
        private void Example_ConfigureExistingStorageProvider(ClusterConfiguration config)
        {
            IProviderConfiguration storageProvider = null;

            const string myProviderFullTypeName = "Orleans.Storage.AzureTableStorage"; // Alternatively, can be something like typeof(AzureTableStorage).FullName
            const string myProviderName = "AzureStore"; // what ever arbitrary name you want to give to your provider
            if (config.Globals.TryGetProviderConfiguration(myProviderFullTypeName, myProviderName, out storageProvider))
            {
                // provider configuration already exists, modify it.
                string connectionString = RoleEnvironment.GetConfigurationSettingValue(DATA_CONNECTION_STRING_KEY);
                storageProvider.SetProperty(DATA_CONNECTION_STRING_KEY, connectionString);
                storageProvider.SetProperty("MyCustomProperty1", "MyCustomPropertyValue1");
            }
            else
            {
                // provider configuration does not exists, add a new one.
                var properties = new Dictionary<string, string>();
                string connectionString = RoleEnvironment.GetConfigurationSettingValue(DATA_CONNECTION_STRING_KEY);
                properties.Add(DATA_CONNECTION_STRING_KEY, connectionString);
                properties.Add("MyCustomProperty2", "MyCustomPropertyValue2");

                config.Globals.RegisterStorageProvider(myProviderFullTypeName, myProviderName, properties);
            }

            // Alternatively, find all storage providers and modify them as necessary
            foreach (IProviderConfiguration providerConfig in config.Globals.GetAllProviderConfigurations())//storageConfiguration.Providers.Values.Where(provider => provider is ProviderConfiguration).Cast<ProviderConfiguration>())
            {
                if (providerConfig.Type.Equals(myProviderFullTypeName))
                {
                    string connectionString = RoleEnvironment.GetConfigurationSettingValue(DATA_CONNECTION_STRING_KEY);
                    providerConfig.SetProperty(DATA_CONNECTION_STRING_KEY, connectionString);
                    providerConfig.SetProperty("MyCustomProperty3", "MyCustomPropertyValue3");
                }
            }

            // Once silo starts you can see that it prints in the log:
            //   Providers:
            //      StorageProviders:
            //          Name=AzureStore, Type=Orleans.Storage.AzureTableStorage, Properties=[DataConnectionString, MyCustomProperty, MyCustomProperty1, MyCustomProperty3]
        }

        // Below is an example of how to define a full configuration for a new storage provider that is not already specified in the config file.
        private void Example_ConfigureNewStorageProvider(ClusterConfiguration config)
        {
            const string myProviderFullTypeName = "Orleans.Storage.AzureTableStorage"; // Alternatively, can be something like typeof(AzureTableStorage).FullName
            const string myProviderName = "MyNewAzureStoreProvider"; // what ever arbitrary name you want to give to your provider

            var properties = new Dictionary<string, string>();
            string connectionString = RoleEnvironment.GetConfigurationSettingValue(DATA_CONNECTION_STRING_KEY);
            properties.Add(DATA_CONNECTION_STRING_KEY, connectionString);
            properties.Add("MyCustomProperty4", "MyCustomPropertyValue4");

            config.Globals.RegisterStorageProvider(myProviderFullTypeName, myProviderName, properties);

            // Once silo starts you can see that it prints in the log:
            //  Providers:
            //      StorageProviders:
            //          Name=MyNewAzureStoreProvider, Type=Orleans.Storage.AzureTableStorage, Properties=[DataConnectionString, MyCustomProperty4]
        }

        // Below is an example of how to define a full configuration for a new Bootstrap provider that is not already specified in the config file.
        private void Example_ConfigureNewBootstrapProvider(ClusterConfiguration config)
        {
            //const string myProviderFullTypeName = "FullNameSpace.NewBootstrapProviderType"; // Alternatively, can be something like typeof(EventStoreInitBootstrapProvider).FullName
            //const string myProviderName = "MyNewBootstrapProvider"; // what ever arbitrary name you want to give to your provider
            //var properties = new Dictionary<string, string>();
            
            //config.Globals.RegisterBootstrapProvider(myProviderFullTypeName, myProviderName, properties);

            // The last line, config.Globals.RegisterBootstrapProvider, is commented out because the assembly with "FullNameSpace.NewBootstrapProviderType" is not added to the project,
            // this the silo will fail to load the new bootstrap provider upon startup.
            // !!!!!!!!!! Provider of type FullNameSpace.NewBootstrapProviderType name MyNewBootstrapProvider was not loaded.
            // Once you add your new provider to the project, uncommnet this line.

            // Once silo starts you can see that it prints in the log:
            // Providers:
            //      BootstrapProviders:
            //          Name=MyNewBootstrapProvider, Type=FullNameSpace.NewBootstrapProviderType, Properties=[]
        }
    }
}
