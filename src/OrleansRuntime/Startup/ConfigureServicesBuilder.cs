using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Startup
{
    internal class ConfigureServicesBuilder
    {
        public ConfigureServicesBuilder(MethodInfo configureServices)
        {
            if (configureServices == null)
            {
                throw new ArgumentNullException(nameof(configureServices));
            }

            // Only support IServiceCollection parameters
            var parameters = configureServices.GetParameters();
            if (parameters.Length > 1 ||
                parameters.Any(p => p.ParameterType != typeof(IServiceCollection)))
            {
                throw new InvalidOperationException("ConfigureServices can take at most a single IServiceCollection parameter.");
            }

            MethodInfo = configureServices;
        }

        public IServiceProvider Build (object instance, IServiceCollection services)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            return Invoke(instance, services);
        }

        private IServiceProvider Invoke(object instance, IServiceCollection exportServices)
        {
            var parameters = new object[MethodInfo.GetParameters().Length];

            // Ctor ensures we have at most one IServiceCollection parameter
            if (parameters.Length > 0)
            {
                parameters[0] = exportServices;
            }

            //
            // For Orleans we've a a modified behavior, different from Asp.Net vNext, since Orleans will not fallback to
            // default DI implementation if the ConfigureServices method is not returning a build DI container.
            //

            var serviceProvider = MethodInfo.Invoke(instance, parameters) as IServiceProvider;

            if (serviceProvider == null)
            {
                throw new InvalidOperationException("The ConfigureServices method did not returned a configured IServiceProvider instance.");
            }

            return serviceProvider;
        }

        public MethodInfo MethodInfo { get; }
    }
}
