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
using System.Linq;
using System.Net;
using System.Reflection;
using Orleans.Streams;

namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Interfacse exposed by ServiceRuntimeWrapper for functionality provided 
    /// by Microsoft.WindowsAzure.ServiceRuntime.
    ///  </summary>
    public interface IServiceRuntimeWrapper
    {
        /// <summary>
        /// Deployment ID of the hosted service
        /// </summary>
        string DeploymentId { get; }

        /// <summary>
        /// Name of the role instance
        /// </summary>
        string InstanceName { get; }

        /// <summary>
        /// Name of the worker/web role
        /// </summary>
        string RoleName { get; }

        /// <summary>
        /// Update domain of the role instance
        /// </summary>
        int UpdateDomain { get; }
        
        /// <summary>
        /// Fault domain of the role instance
        /// </summary>
        int FaultDomain { get; }

        /// <summary>
        /// Number of instances in the worker/web role
        /// </summary>
        int RoleInstanceCount { get; }

        /// <summary>
        /// Returns IP endpoint by name
        /// </summary>
        /// <param name="endpointName">Name of the IP endpoint</param>
        /// <returns></returns>
        IPEndPoint GetIPEndpoint(string endpointName);

        /// <summary>
        /// Returns value of the given configuration setting
        /// </summary>
        /// <param name="configurationSettingName"></param>
        /// <returns></returns>
        string GetConfigurationSettingValue(string configurationSettingName);

        /// <summary>
        /// Subscribes given even handler for role instance Stopping event
        /// </summary>
        /// /// <param name="handlerObject">Object that handler is part of, or null for a static method</param>
        /// <param name="handler">Handler to subscribe</param>
        void SubscribeForStoppingNotifcation(object handlerObject, EventHandler<object> handler);

        /// <summary>
        /// Unsubscribes given even handler from role instance Stopping event
        /// </summary>
        /// /// <param name="handlerObject">Object that handler is part of, or null for a static method</param>
        /// <param name="handler">Handler to unsubscribe</param>
        void UnsubscribeFromStoppingNotifcation(object handlerObject, EventHandler<object> handler);
    }


    /// <summary>
    /// The purpose of this class is to wrap the functionality provided 
    /// by Microsoft.WindowsAzure.ServiceRuntime.dll, so that we can access it via Reflection,
    /// and not have a compile-time dependency on it.
    /// Microsoft.WindowsAzure.ServiceRuntime.dll doesn't have an official NuGet package.
    /// By loading it via Reflection we solve this problem, and do not need an assembly 
    /// binding redirect for it, as we can call any compatible version.
    /// Microsoft.WindowsAzure.ServiceRuntime.dll hasn't changed in years, so the chance of a breaking change
    /// is relatively low.
    /// </summary>
    internal class ServiceRuntimeWrapper : IServiceRuntimeWrapper, IDeploymentConfiguration
    {
        private readonly TraceLogger logger;
        private Assembly assembly;
        private Type roleEnvironmentType;
        private EventInfo stoppingEvent;
        private MethodInfo stoppingEventAdd;
        private MethodInfo stoppingEventRemove;
        private Type roleInstanceType;
        private dynamic currentRoleInstance;
        private dynamic instanceEndpoints;
        private dynamic role;


        public ServiceRuntimeWrapper()
        {
            logger = TraceLogger.GetLogger("ServiceRuntimeWrapper");
            Initialize();
        }

        public string DeploymentId { get; private set; }
        public string InstanceId { get; private set; }
        public string RoleName { get; private set; }
        public int UpdateDomain { get; private set; }
        public int FaultDomain { get; private set; }

        public string InstanceName
        {
            get { return ExtractInstanceName(InstanceId, DeploymentId); }
        }

        public int RoleInstanceCount
        {
            get
            {
                dynamic instances = role.Instances;
                return instances.Count;
            }
        }

        public IList<string> GetAllSiloInstanceNames()
        {
            dynamic instances = role.Instances;
            var list = new List<string>();
            foreach(dynamic instance in instances)
                list.Add(ExtractInstanceName(instance.Id,DeploymentId));
            
            return list;
        }

        public IPEndPoint GetIPEndpoint(string endpointName)
        {
            try
            {
                dynamic ep = instanceEndpoints.GetType()
                    .GetProperty("Item")
                    .GetMethod.Invoke(instanceEndpoints, new object[] {endpointName});
                return ep.IPEndpoint;
            }
            catch (Exception exc)
            {
                var errorMsg = string.Format("Unable to obtain endpoint info for role {0} from role config parameter {1} -- Endpoints defined = [{2}]",
                    RoleName, endpointName, string.Join(", ", instanceEndpoints));

                logger.Error(ErrorCode.SiloEndpointConfigError, errorMsg, exc);
                throw new OrleansException(errorMsg, exc);
            }
        }

        public string GetConfigurationSettingValue(string configurationSettingName)
        {
            return (string) roleEnvironmentType.GetMethod("GetConfigurationSettingValue").Invoke(null, new object[] {configurationSettingName});
        }

        public void SubscribeForStoppingNotifcation(object handlerObject, EventHandler<object> handler)
        {
            var handlerDelegate = handler.GetMethodInfo().CreateDelegate(stoppingEvent.EventHandlerType, handlerObject);
            stoppingEventAdd.Invoke(null, new object[] { handlerDelegate });
            
        }

        public void UnsubscribeFromStoppingNotifcation(object handlerObject, EventHandler<object> handler)
        {
            var handlerDelegate = handler.GetMethodInfo().CreateDelegate(stoppingEvent.EventHandlerType, handlerObject);
            stoppingEventRemove.Invoke(null, new[] { handlerDelegate });
        }


        private void Initialize()
        {
            assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                a => a.FullName.StartsWith("Microsoft.WindowsAzure.ServiceRuntime"));

            // If we are runing within a worker role Microsoft.WindowsAzure.ServiceRuntime should already be loaded
            if (assembly == null)
            {
                const string msg1 = "Microsoft.WindowsAzure.ServiceRuntime is not loaded. Trying to load it with Assembly.LoadWithPartialName().";
                logger.Warn(ErrorCode.AzureServiceRuntime_NotLoaded, msg1);

                // Microsoft.WindowsAzure.ServiceRuntime isn't loaded. We may be running within a web role or not in Azure.
                // Trying to load by partial name, so that we are not version specific.
                // Assembly.LoadWithPartialName has been deprecated. Is there a better way to load any version of a known assembly?
                #pragma warning disable 618
                assembly = Assembly.LoadWithPartialName("Microsoft.WindowsAzure.ServiceRuntime");
                #pragma warning restore 618
                if (assembly == null)
                {
                    const string msg2 = "Failed to find or load Microsoft.WindowsAzure.ServiceRuntime.";
                    logger.Error(ErrorCode.AzureServiceRuntime_FailedToLoad, msg2);
                    throw new OrleansException(msg2);
                }
            }

            roleEnvironmentType = assembly.GetType("Microsoft.WindowsAzure.ServiceRuntime.RoleEnvironment");
            stoppingEvent = roleEnvironmentType.GetEvent("Stopping");
            stoppingEventAdd = stoppingEvent.GetAddMethod();
            stoppingEventRemove = stoppingEvent.GetRemoveMethod();

            roleInstanceType = assembly.GetType("Microsoft.WindowsAzure.ServiceRuntime.RoleInstance");

            DeploymentId = (string) roleEnvironmentType.GetProperty("DeploymentId").GetValue(null);
            if (string.IsNullOrWhiteSpace(DeploymentId))
                throw new OrleansException("DeploymentId is null or whitespace.");

            currentRoleInstance = roleEnvironmentType.GetProperty("CurrentRoleInstance").GetValue(null);
            if (currentRoleInstance == null)
                throw new OrleansException("CurrentRoleInstance is null.");

            InstanceId = currentRoleInstance.Id;
            UpdateDomain = currentRoleInstance.UpdateDomain;
            FaultDomain = currentRoleInstance.FaultDomain;
            instanceEndpoints = currentRoleInstance.InstanceEndpoints;
            role = currentRoleInstance.Role;
            RoleName = role.Name;
        }

        private static string ExtractInstanceName(string instanceId, string deploymentId)
        {
            return instanceId.Length > deploymentId.Length && instanceId.StartsWith(deploymentId)
                ? instanceId.Substring(deploymentId.Length + 1)
                : instanceId;
        }
    }
}