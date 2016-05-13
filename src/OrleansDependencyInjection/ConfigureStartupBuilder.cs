using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime.Management;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.ReminderService;
using Orleans.Storage;
using Orleans.Streams;

namespace Orleans.Runtime.Startup
{
    /// <summary>
    /// Configure dependency injection at startup
    /// </summary>
    public class ConfigureStartupBuilder : IStartupBuilder
    {
        internal static IServiceProvider ConfigureStartup(string startupTypeName)
        {
            if (String.IsNullOrWhiteSpace(startupTypeName))
            {
                return new DefaultServiceProvider();
            }

            var startupType = Type.GetType(startupTypeName);

            if (startupType == null)
            {
                throw new InvalidOperationException(string.Format("Can not locate the type specified in the configuration file: '{0}'.", startupTypeName));
            }

            var servicesMethod = FindConfigureServicesDelegate(startupType);

            if ((servicesMethod != null && !servicesMethod.MethodInfo.IsStatic))
            {
                var instance = Activator.CreateInstance(startupType);

                IServiceCollection serviceCollection = RegisterSystemTypes();
                return servicesMethod.Build(instance, serviceCollection);
            }
            return new DefaultServiceProvider();
        }

        private static IServiceCollection RegisterSystemTypes()
        {
            //
            // Register the system classes and grains in this method.
            //

            IServiceCollection serviceCollection = new ServiceCollection();

            serviceCollection.AddTransient<ManagementGrain>();
            serviceCollection.AddTransient<GrainBasedMembershipTable>();
            serviceCollection.AddTransient<GrainBasedReminderTable>();
            serviceCollection.AddTransient<PubSubRendezvousGrain>();
            serviceCollection.AddTransient<MemoryStorageGrain>();

            return serviceCollection;
        }

        private static ConfigureServicesBuilder FindConfigureServicesDelegate(Type startupType)
        {
            var servicesMethod = FindMethod(startupType, "ConfigureServices", typeof(IServiceProvider), required: false);

            return servicesMethod == null ? null : new ConfigureServicesBuilder(servicesMethod);
        }

        private static MethodInfo FindMethod(Type startupType, string methodName, Type returnType = null, bool required = true)
        {
            var methods = startupType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            var selectedMethods = methods.Where(method => method.Name.Equals(methodName)).ToList();

            if (selectedMethods.Count > 1)
            {
                throw new InvalidOperationException(string.Format("Having multiple overloads of method '{0}' is not supported.", methodName));
            }

            var methodInfo = selectedMethods.FirstOrDefault();

            if (methodInfo == null)
            {
                if (required)
                {
                    throw new InvalidOperationException(string.Format("A method named '{0}' in the type '{1}' could not be found.",
                        methodName,
                        startupType.FullName));
                }

                return null;
            }

            if (returnType != null && methodInfo.ReturnType != returnType)
            {
                if (required)
                {
                    throw new InvalidOperationException(string.Format("The '{0}' method in the type '{1}' must have a return type of '{2}'.",
                        methodInfo.Name,
                        startupType.FullName,
                        returnType.Name));
                }

                return null;
            }

            return methodInfo;
        }

        IServiceProvider IStartupBuilder.ConfigureStartup(string startupTypeName)
        {
            return ConfigureStartupBuilder.ConfigureStartup(startupTypeName);
        }
    }
}
