using System;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ISiloHost = Orleans.Hosting.ISiloHost;
using ISiloBuilder = Orleans.Hosting.ISiloBuilder;
using ISiloHostBuilder = Orleans.Hosting.ISiloHostBuilder;
using SiloHostBuilderContext = Orleans.Hosting.HostBuilderContext;
using IHostingEnvironment = Orleans.Hosting.IHostingEnvironment;

namespace Orleans.TestingHost
{
    internal class SiloHostBuilderAdaptor : ISiloHostBuilder
    {
        private readonly IHostBuilder hostBuilder;
        private readonly ISiloBuilder siloBuilder;

        public SiloHostBuilderAdaptor(IHostBuilder hostBuilder, ISiloBuilder siloBuilder)
        {
            this.hostBuilder = hostBuilder;
            this.siloBuilder = siloBuilder;
        }

        public IDictionary<object, object> Properties => this.hostBuilder.Properties;

        public ISiloHost Build() => throw new NotSupportedException(
            $"This implementation of {nameof(ISiloHostBuilder)} is designed for use with {nameof(TestClusterBuilder)} only and therefore this method is not supported");

        public ISiloHostBuilder ConfigureAppConfiguration(Action<SiloHostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            this.hostBuilder.ConfigureAppConfiguration((ctx, containerBuilder) => configureDelegate(this.GetContext(ctx), containerBuilder));
            return this;
        }

        public ISiloHostBuilder ConfigureContainer<TContainerBuilder>(Action<SiloHostBuilderContext, TContainerBuilder> configureDelegate)
        {
            this.hostBuilder.ConfigureContainer<TContainerBuilder>((ctx, containerBuilder) => configureDelegate(this.GetContext(ctx), containerBuilder));
            return this;
        }

        public ISiloHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
        {
            this.hostBuilder.ConfigureHostConfiguration(configureDelegate);
            return this;
        }

        public ISiloHostBuilder ConfigureServices(Action<SiloHostBuilderContext, IServiceCollection> configureDelegate)
        {
            this.siloBuilder.ConfigureServices((ctx, services) => configureDelegate(this.GetContext(ctx), services));
            return this;
        }

        public ISiloHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        {
            this.hostBuilder.UseServiceProviderFactory(factory);
            return this;
        }

        private SiloHostBuilderContext GetContext(HostBuilderContext context)
        {
            const string key = "SiloHostBuilderContext";
            if (this.Properties.TryGetValue(key, out var result) && result is SiloHostBuilderContext resultContext)
            {
                return resultContext;
            }

            resultContext = new SiloHostBuilderContext(context.Properties)
            {
                Configuration = context.Configuration,
                HostingEnvironment = new HostingEnvironmentWrapper(context.HostingEnvironment)
            };

            this.Properties[key] = resultContext;
            return resultContext;
        }

        private class HostingEnvironmentWrapper : IHostingEnvironment
        {
            private readonly IHostEnvironment hostEnvironment;

            public HostingEnvironmentWrapper(IHostEnvironment hostEnvironment) => this.hostEnvironment = hostEnvironment;

            public string EnvironmentName
            {
                get => this.hostEnvironment.EnvironmentName;
                set => this.hostEnvironment.EnvironmentName = value;
            }

            public string ApplicationName
            {
                get => this.hostEnvironment.ApplicationName;
                set => this.hostEnvironment.ApplicationName = value;
            }
        }
    }
}
