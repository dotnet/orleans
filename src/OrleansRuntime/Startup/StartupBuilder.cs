using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Startup
{
    using Orleans.Runtime.Configuration;

    /// <summary>
    /// Configure dependency injection at startup
    /// </summary>
    internal class StartupBuilder
    {
        internal static IServiceProvider ConfigureStartup(NodeConfiguration config, Action<IServiceCollection> configureServices, out bool usingCustomServiceProvider)
        {
            if (config.ServiceProviderBuilder != null)
            {
                if (!string.IsNullOrWhiteSpace(config.StartupTypeName))
                {
                    throw new ArgumentException($"Only one of {nameof(config.ServiceProviderBuilder)} and {nameof(config.StartupTypeName)} may be specified.");
                }

                return ConfigureStartup(config.ServiceProviderBuilder, configureServices, out usingCustomServiceProvider);
            }

            return ConfigureStartup(config.StartupTypeName, configureServices, out usingCustomServiceProvider);
        }

        private static IServiceProvider ConfigureStartup(string startupTypeName, Action<IServiceCollection> configureServices, out bool usingCustomServiceProvider)
        {
            var hasValidServiceBuilderMethod = false;
            ConfigureServicesBuilder servicesMethod = null;
            Type startupType = null;

            if (!string.IsNullOrWhiteSpace(startupTypeName))
            {
                startupType = Type.GetType(startupTypeName);
                if (startupType == null)
                {
                    throw new InvalidOperationException($"Can not locate the type specified in the configuration file: '{startupTypeName}'.");
                }

                servicesMethod = FindConfigureServicesDelegate(startupType);
                if (servicesMethod != null && !servicesMethod.MethodInfo.IsStatic)
                {
                    hasValidServiceBuilderMethod = true;
                }
            }
            Func<IServiceCollection, IServiceProvider> serviceProviderBuilder = null;
            if (hasValidServiceBuilderMethod)
            {
                var instance = Activator.CreateInstance(startupType);
                serviceProviderBuilder = serviceCollection => servicesMethod.Build(instance, serviceCollection);
            }

            return ConfigureStartup(serviceProviderBuilder, configureServices, out usingCustomServiceProvider);
        }

        private static IServiceProvider ConfigureStartup(Func<IServiceCollection, IServiceProvider> serviceProviderBuilder, Action<IServiceCollection> configureServices, out bool usingCustomServiceProvider)
        {
            IServiceCollection serviceCollection = new ServiceCollection();
            configureServices(serviceCollection);

            usingCustomServiceProvider = serviceProviderBuilder != null;
            if (usingCustomServiceProvider)
            {
                return serviceProviderBuilder(serviceCollection);
            }

            return serviceCollection.BuildServiceProvider();
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
