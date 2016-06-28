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
    internal class StartupBuilder
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
                throw new InvalidOperationException($"Can not locate the type specified in the configuration file: '{startupTypeName}'.");
            }

            var servicesMethod = FindConfigureServicesDelegate(startupType);

            if ((servicesMethod != null && !servicesMethod.MethodInfo.IsStatic))
            {
                var instance = Activator.CreateInstance(startupType);

                var serviceCollection = RegisterSystemTypes();

                return servicesMethod.Build(instance, serviceCollection);
            }

            //
            // If a Startup Type is configured and does not have the required method, return null, it is
            // the caller's responsibility to handle it as required. In our case it will create the default
            // provider. At this point we should not do that.
            //

            return null;
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
            var servicesMethod = FindMethod(startupType, "ConfigureServices", typeof(IServiceProvider), false);

            return servicesMethod == null ? null : new ConfigureServicesBuilder(servicesMethod);
        }

        private static MethodInfo FindMethod(Type startupType, string methodName, Type returnType = null,
            bool required = true)
        {
            var methods = startupType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            var selectedMethods = methods.Where(method => method.Name.Equals(methodName)).ToList();

            if (selectedMethods.Count > 1)
            {
                throw new InvalidOperationException($"Having multiple overloads of method '{methodName}' is not supported.");
            }

            var methodInfo = selectedMethods.FirstOrDefault();

            if (methodInfo == null)
            {
                if (required)
                {
                    throw new InvalidOperationException($"A method named '{methodName}' in the type '{startupType.FullName}' could not be found.");
                }

                return null;
            }

            if (returnType != null && methodInfo.ReturnType != returnType)
            {
                if (required)
                {
                    throw new InvalidOperationException($"The '{methodInfo.Name}' method in the type '{startupType.FullName}' must have a return type of '{returnType.Name}'.");
                }

                return null;
            }

            return methodInfo;
        }
    }
}
