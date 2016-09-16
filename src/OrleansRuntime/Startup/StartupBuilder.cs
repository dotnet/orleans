using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.ReminderService;

namespace Orleans.Runtime.Startup
{
    /// <summary>
    /// Configure dependency injection at startup
    /// </summary>
    internal class StartupBuilder
    {
        internal static IServiceProvider ConfigureStartup(string startupTypeName, out bool usingCustomServiceProvider)
        {
            usingCustomServiceProvider = false;
            IServiceCollection serviceCollection = new ServiceCollection();
            ConfigureServicesBuilder servicesMethod = null;
            Type startupType = null;

            if (!String.IsNullOrWhiteSpace(startupTypeName))
            {
                startupType = Type.GetType(startupTypeName);
                if (startupType == null)
                {
                    throw new InvalidOperationException($"Can not locate the type specified in the configuration file: '{startupTypeName}'.");
                }

                servicesMethod = FindConfigureServicesDelegate(startupType);
                if (servicesMethod != null && !servicesMethod.MethodInfo.IsStatic)
                {
                    usingCustomServiceProvider = true;
                }
            }

            RegisterSystemTypes(serviceCollection);

            if (usingCustomServiceProvider)
            {
                var instance = Activator.CreateInstance(startupType);
                return servicesMethod.Build(instance, serviceCollection);
            }

            return serviceCollection.BuildServiceProvider();
        }

        private static void RegisterSystemTypes(IServiceCollection serviceCollection)
        {
            // add system types
            // Note: you can replace IGrainFactory with your own implementation, but 
            // we don't recommend it, in the aspect of performance and usability
            serviceCollection.AddSingleton<GrainFactory>((_sp) => new GrainFactory());
            serviceCollection.AddSingleton<IGrainFactory>((sp) => sp.GetService<GrainFactory>());
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
