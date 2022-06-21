using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Interface exposed by ServiceRuntimeWrapper for functionality provided 
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
        void SubscribeForStoppingNotification(object handlerObject, EventHandler<object> handler);

        /// <summary>
        /// Unsubscribes given even handler from role instance Stopping event
        /// </summary>
        /// /// <param name="handlerObject">Object that handler is part of, or null for a static method</param>
        /// <param name="handler">Handler to unsubscribe</param>
        void UnsubscribeFromStoppingNotification(object handlerObject, EventHandler<object> handler);
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
        private readonly ILogger logger;
        private Assembly assembly;
        private Type roleEnvironmentType;
        private EventInfo stoppingEvent;
        private MethodInfo stoppingEventAdd;
        private MethodInfo stoppingEventRemove;
        private Type roleInstanceType;
        private dynamic currentRoleInstance;
        private dynamic instanceEndpoints;
        private dynamic role;


        public ServiceRuntimeWrapper(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<ServiceRuntimeWrapper>();
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

        public IList<string> GetAllSiloNames()
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
                var endpointNames = (string)string.Join(", ", instanceEndpoints);
                logger.LogError(
                    (int)ErrorCode.SiloEndpointConfigError,
                    exc,
                    "Unable to obtain endpoint info for role {RoleName} from role config parameter {EndpointName} -- Endpoints defined = [{EndpointNames}]",
                    RoleName,
                    endpointName,
                    endpointNames);

                throw new OrleansException(
                    $"Unable to obtain endpoint info for role {RoleName} from role config parameter {endpointName} -- Endpoints defined = [{endpointNames}]",
                    exc);
            }
        }

        public string GetConfigurationSettingValue(string configurationSettingName)
        {
            return (string) roleEnvironmentType.GetMethod("GetConfigurationSettingValue").Invoke(null, new object[] {configurationSettingName});
        }

        public void SubscribeForStoppingNotification(object handlerObject, EventHandler<object> handler)
        {
            var handlerDelegate = handler.GetMethodInfo().CreateDelegate(stoppingEvent.EventHandlerType, handlerObject);
            stoppingEventAdd.Invoke(null, new object[] { handlerDelegate });
            
        }

        public void UnsubscribeFromStoppingNotification(object handlerObject, EventHandler<object> handler)
        {
            var handlerDelegate = handler.GetMethodInfo().CreateDelegate(stoppingEvent.EventHandlerType, handlerObject);
            stoppingEventRemove.Invoke(null, new[] { handlerDelegate });
        }


        private void Initialize()
        {
            assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                a => a.FullName.StartsWith("Microsoft.WindowsAzure.ServiceRuntime", StringComparison.Ordinal));

            // If we are runing within a worker role Microsoft.WindowsAzure.ServiceRuntime should already be loaded
            if (assembly == null)
            {
                const string msg1 = "Microsoft.WindowsAzure.ServiceRuntime is not loaded. Trying to load it with Assembly.LoadWithPartialName().";
                logger.LogWarning((int)ErrorCode.AzureServiceRuntime_NotLoaded, msg1);

                // Microsoft.WindowsAzure.ServiceRuntime isn't loaded. We may be running within a web role or not in Azure.
#pragma warning disable 618
                assembly = Assembly.Load(new AssemblyName("Microsoft.WindowsAzure.ServiceRuntime, Version = 2.7.0.0, Culture = neutral, PublicKeyToken = 31bf3856ad364e35"));
#pragma warning restore 618
                if (assembly == null)
                {
                    const string msg2 = "Failed to find or load Microsoft.WindowsAzure.ServiceRuntime.";
                    logger.LogError((int)ErrorCode.AzureServiceRuntime_FailedToLoad, msg2);
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
            return instanceId.Length > deploymentId.Length && instanceId.StartsWith(deploymentId, StringComparison.Ordinal)
                ? instanceId.Substring(deploymentId.Length + 1)
                : instanceId;
        }
    }
}
