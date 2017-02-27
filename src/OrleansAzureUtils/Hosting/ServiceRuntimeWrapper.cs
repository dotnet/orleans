using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using Orleans.Streams;
#if !NETSTANDARD
using Microsoft.Azure;
#endif
using System.IO;

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
        /// Returns value of the given configuration setting or null if not found.
        /// </summary>
        /// <param name="configurationSettingName"></param>
        /// <returns></returns>
#if !NETSTANDARD
        [Obsolete("Update your calls to Microsoft.Azure.CloudConfigurationManager.GetSetting, it provides automatic fallback to System.Configuration.ConfigurationManager to read from Appsettings.")]
#endif
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
        private readonly Logger logger;
        private EventInfo stoppingEvent;
        private MethodInfo stoppingEventAdd;
        private MethodInfo stoppingEventRemove;
        private Type roleEnvironmentExceptionType;          // Exception thrown for missing settings.
        private MethodInfo getServiceSettingMethod;         // Method for getting values from the service configuration.
        private dynamic currentRoleInstance;
        private dynamic instanceEndpoints;
        private dynamic role;

        private const string RoleEnvironmentTypeName = "Microsoft.WindowsAzure.ServiceRuntime.RoleEnvironment";
        private const string RoleEnvironmentExceptionTypeName = "Microsoft.WindowsAzure.ServiceRuntime.RoleEnvironmentException";
        private const string IsAvailablePropertyName = "IsAvailable";
        private const string GetSettingValueMethodName = "GetConfigurationSettingValue";

        // Keep this array sorted by the version in the descendant order.
        private readonly string[] knownAssemblyNames = new string[]
        {
            "Microsoft.WindowsAzure.ServiceRuntime, Culture=neutral, PublicKeyToken=31bf3856ad364e35, ProcessorArchitecture=MSIL"
        };

        public ServiceRuntimeWrapper()
        {
            logger = LogManager.GetLogger("ServiceRuntimeWrapper");
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
                string errorMsg = string.Format("Unable to obtain endpoint info for role {0} from role config parameter {1} -- Endpoints defined = [{2}]",
                    RoleName, endpointName, string.Join(", ", instanceEndpoints));

                logger.Error(ErrorCode.SiloEndpointConfigError, errorMsg, exc);
                throw new OrleansException(errorMsg, exc);
            }
        }

#if !NETSTANDARD
        [Obsolete("Update your calls to Microsoft.Azure.CloudConfigurationManager.GetSetting, it provides automatic fallback to System.Configuration.ConfigurationManager to read from Appsettings.")]
        public string GetConfigurationSettingValue(string configurationSettingName)
        {
            return CloudConfigurationManager.GetSetting(configurationSettingName, false, false);
        }
#else
        public string GetConfigurationSettingValue(string configurationSettingName)
        {
            return GetServiceRuntimeSetting(configurationSettingName);
        }
#endif

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
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                a => a.FullName.StartsWith("Microsoft.WindowsAzure.ServiceRuntime"));

#if !NETSTANDARD
            // Try to load from GAC, took from Azure .Net SDK: https://github.com/Azure/azure-sdk-for-net/blob/master/src/Common/Configuration/AzureApplicationSettings.cs
            if (assembly == null)
            {
                assembly = GetServiceRuntimeAssembly();
            }
#endif

            // If we are runing within a worker role Microsoft.WindowsAzure.ServiceRuntime should already be loaded
            if (assembly == null)
            {
                const string msg1 = "Microsoft.WindowsAzure.ServiceRuntime is not loaded. Trying to load it with Assembly.LoadWithPartialName().";
                logger.Warn(ErrorCode.AzureServiceRuntime_NotLoaded, msg1);

                // Microsoft.WindowsAzure.ServiceRuntime isn't loaded. We may be running within a web role or not in Azure.
#pragma warning disable 618
                assembly = Assembly.Load(new AssemblyName("Microsoft.WindowsAzure.ServiceRuntime, Version=2.7.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"));
#pragma warning restore 618

                if (assembly == null)
                {
                    const string msg2 = "Failed to find or load Microsoft.WindowsAzure.ServiceRuntime.";
                    logger.Error(ErrorCode.AzureServiceRuntime_FailedToLoad, msg2);
                    throw new OrleansException(msg2);
                }
            }

            var roleEnvironmentType = assembly.GetType(RoleEnvironmentTypeName);

            roleEnvironmentExceptionType = assembly.GetType(RoleEnvironmentExceptionTypeName);

            var isAvailable = false;

            if (roleEnvironmentType != null)
            {
                var isAvailableProperty = roleEnvironmentType.GetProperty(IsAvailablePropertyName);

                try
                {
                    isAvailable = isAvailableProperty != null && (bool)isAvailableProperty.GetValue(null, new object[] { });
                }
                catch (TargetInvocationException e)
                {
                    // Running service runtime code from an application targeting .Net 4.0 results
                    // in a type initialization exception unless application's configuration file
                    // explicitly enables v2 runtime activation policy. In this case we should fall
                    // back to the web.config/app.config file.
                    if (!(e.InnerException is TypeInitializationException))
                    {
                        throw;
                    }

                    isAvailable = false;
                }
            }
            else
            {
                const string msg3 = "Failed to get type RoleEnvironment from ServiceRuntime assembly.";
                logger.Error(ErrorCode.AzureServiceRuntime_FailedToLoad, msg3);
                throw new OrleansException(msg3);
            }

            if (isAvailable)
            {
                stoppingEvent = roleEnvironmentType.GetEvent("Stopping");
                stoppingEventAdd = stoppingEvent.GetAddMethod();
                stoppingEventRemove = stoppingEvent.GetRemoveMethod();

                DeploymentId = (string)roleEnvironmentType.GetProperty("DeploymentId").GetValue(null);
                if (string.IsNullOrWhiteSpace(DeploymentId))
                    throw new OrleansException("DeploymentId is null or whitespace.");

                getServiceSettingMethod = roleEnvironmentType.GetMethod(GetSettingValueMethodName, 
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod);

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
        }

        private static string ExtractInstanceName(string instanceId, string deploymentId)
        {
            return instanceId.Length > deploymentId.Length && instanceId.StartsWith(deploymentId)
                ? instanceId.Substring(deploymentId.Length + 1)
                : instanceId;
        }

#if !NETSTANDARD
        private Assembly GetServiceRuntimeAssembly()
        {
            Assembly assembly = null;

            foreach (string assemblyName in knownAssemblyNames)
            {
                string assemblyPath = NativeMethods.GetAssemblyPath(assemblyName);

                try
                {
                    if (!string.IsNullOrEmpty(assemblyPath))
                    {
                        assembly = Assembly.LoadFrom(assemblyPath);
                    }
                }
                catch (Exception ex)
                {
                    // The following exceptions are ignored for enabling configuration manager to proceed
                    // and load the configuration from application settings instead of using ServiceRuntime.
                    if (!(ex is FileNotFoundException ||
                          ex is FileLoadException ||
                          ex is BadImageFormatException))
                    {
                        throw;
                    }
                }
            }

            return assembly;
        }
#endif

#if NETSTANDARD
        private bool IsMissingSettingException(Exception e)
        {
            if (e == null)
            {
                return false;
            }
            Type type = e.GetType();

            return object.ReferenceEquals(type, roleEnvironmentExceptionType)
                || type.GetTypeInfo().IsSubclassOf(roleEnvironmentExceptionType);
        }

        private string GetServiceRuntimeSetting(string name)
        {
            Debug.Assert(!string.IsNullOrEmpty(name));

            string value = null;

            if (getServiceSettingMethod != null)
            {
                try
                {
                    value = (string)getServiceSettingMethod.Invoke(null, new object[] { name });
                }
                catch (TargetInvocationException e)
                {
                    if (!IsMissingSettingException(e.InnerException))
                    {
                        throw;
                    }
                }
            }
            return value;
        }
#endif
    }
}
