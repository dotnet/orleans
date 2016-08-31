using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.ReminderService;

namespace Orleans.Runtime.Startup
{
    // <summary>
    // extension methods to register services required internal Orleans methods
    // </summary>
    public static class OrleansInternalServices
    {

        // extension methods to register internal orleans required services
        public static void RegisterSystemTypes(IServiceCollection serviceCollection)
        {
            // Register the system classes and grains in this method.
            // Note: this method will probably have to be moved out into the Silo class to include internal runtime types.
            IGrainFactory grainFactory = new GrainFactory();
            serviceCollection.AddSingleton((GrainFactory)grainFactory);
            //application grain getService by interface type which is public to them, while concret types are internal
            serviceCollection.AddSingleton<IGrainFactory>(grainFactory);
            serviceCollection.AddTransient<IMembershipTable, GrainBasedMembershipTable>();
            serviceCollection.AddTransient<IReminderTable, GrainBasedReminderTable>();
        }

        //default service provider where only contains internal services, mostly used in tests
        public static IServiceProvider DefaultServiceProvider()
        {
            return ServiceProvider;
        }


        private static IServiceProvider BuildDefaultServiceProvider()
        {
            IServiceCollection serviceCollection = new ServiceCollection();
            RegisterSystemTypes(serviceCollection);
            return serviceCollection.BuildServiceProvider();
        }

        private static readonly IServiceProvider ServiceProvider = BuildDefaultServiceProvider();


        //use this method for third party DI containers
        public static IServiceProvider ConfigureStartup(string startupTypeName)
        {
            bool usingCustomServiceProvider = false;
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
