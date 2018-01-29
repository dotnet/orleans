using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Providers;
using Orleans.Runtime.TestHooks;
using Orleans.Configuration;
using Orleans.Logging;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{
    internal class TestClusterHostFactory
    {
        public static ISiloHost CreateSiloHost(string hostName, IEnumerable<IConfigurationSource> configurationSources)
        {
            var configBuilder = new ConfigurationBuilder();
            foreach (var source in configurationSources)
            {
                configBuilder.Add(source);
            }
            var configuration = configBuilder.Build();

            string siloName = configuration["SiloName"] ?? hostName;

            ISiloHostBuilder hostBuilder = new SiloHostBuilder()
                .ConfigureOrleans(ob => ob.Bind(configuration).Configure(options => options.SiloName = options.SiloName ?? siloName))
                .ConfigureHostConfiguration(cb =>
                {
                    // TODO: Instead of passing the sources individually, just chain the pre-built configuration once we upgrade to Microsoft.Extensions.Configuration 2.1
                    foreach (var source in configBuilder.Sources)
                    {
                        cb.Add(source);
                    }
                });

            hostBuilder.Properties["Configuration"] = configuration;
            ConfigureAppServices(configuration, hostBuilder);

            hostBuilder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<TestHooksSystemTarget>();
                ConfigureListeningPorts(context, services);

                TryConfigureTestClusterMembership(context, services);
                TryConfigureFileLogging(configuration, services, siloName);

                // TODO: make SiloHostBuilder work when not using the legacy configuration, similar to what we did with ClientBuilder.
                // All The important information has been migrated to strongly typed options (everything should be migrated, but the minimum required set is already there).
                var clusterConfiguration = GetOrCreateClusterConfiguration(services, context);
                if (Debugger.IsAttached)
                {
                    // Test is running inside debugger - Make timeout ~= infinite
                    clusterConfiguration.Globals.ResponseTimeout = TimeSpan.FromMilliseconds(1000000);
                }
            });

            AddDefaultApplicationParts(hostBuilder.GetApplicationPartManager());

            var host = hostBuilder.Build();
            InitializeTestHooksSystemTarget(host);
            return host;
        }

        public static IClusterClient CreateClusterClient(string hostName, IEnumerable<IConfigurationSource> configurationSources)
        {
            var configBuilder = new ConfigurationBuilder();
            foreach (var source in configurationSources)
            {
                configBuilder.Add(source);
            }
            var configuration = configBuilder.Build();

            var builder = new ClientBuilder();
            builder.Properties["Configuration"] = configuration;
            builder.ConfigureClusterClient(ob => ob.Bind(configuration));
            ConfigureAppServices(configuration, builder);

            builder.ConfigureServices(services =>
            {
                TryConfigureTestClusterMembership(configuration, services);
                TryConfigureFileLogging(configuration, services, hostName);
            });

            AddDefaultApplicationParts(builder.GetApplicationPartManager());
            return builder.Build();
        }

        public static string SerializeConfigurationSources(IList<IConfigurationSource> sources)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                Formatting = Formatting.Indented,
            };

            return JsonConvert.SerializeObject(sources, settings);
        }

        public static IList<IConfigurationSource> DeserializeConfigurationSources(string serializedSources)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                Formatting = Formatting.Indented,
            };

            return JsonConvert.DeserializeObject<IList<IConfigurationSource>>(serializedSources, settings);
        }

        private static void ConfigureListeningPorts(HostBuilderContext context, IServiceCollection services)
        {
            int siloPort = int.Parse(context.Configuration["SiloPort"]);
            int gatewayPort = int.Parse(context.Configuration["GatewayPort"]);

            services.Configure<EndpointOptions>(options =>
            {
                options.IPAddress = IPAddress.Loopback;
                options.Port = siloPort;
                options.ProxyPort = gatewayPort;
            });
        }

        private static ClusterConfiguration GetOrCreateClusterConfiguration(IServiceCollection services, HostBuilderContext context)
        {
            var clusterConfiguration = services
                .FirstOrDefault(s => s.ServiceType == typeof(ClusterConfiguration))
                ?.ImplementationInstance as ClusterConfiguration;

            if (clusterConfiguration == null)
            {
                clusterConfiguration = new ClusterConfiguration
                {
                    Globals =
                    {
                        ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
                    }
                };

                services.AddLegacyClusterConfigurationSupport(clusterConfiguration);
            }
            return clusterConfiguration;
        }

        private static ClientConfiguration GetOrCreateClientConfiguration(IServiceCollection services)
        {
            var clientConfiguration = services.TryGetClientConfiguration();

            if (clientConfiguration == null)
            {
                clientConfiguration = new ClientConfiguration();
                services.AddLegacyClientConfigurationSupport(clientConfiguration);
            }
            return clientConfiguration;
        }

        private static void ConfigureAppServices(IConfiguration configuration, ISiloHostBuilder hostBuilder)
        {
            var builderConfiguratorTypes = configuration.GetSection(nameof(TestClusterOptions.SiloBuilderConfiguratorTypes))?.Get<string[]>();
            if (builderConfiguratorTypes == null) return;

            foreach (var builderConfiguratorType in builderConfiguratorTypes)
            {
                if (!string.IsNullOrWhiteSpace(builderConfiguratorType))
                {
                    var builderConfigurator = (ISiloBuilderConfigurator)Activator.CreateInstance(Type.GetType(builderConfiguratorType, true));
                    builderConfigurator.Configure(hostBuilder);
                }
            }
        }

        private static void ConfigureAppServices(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            var builderConfiguratorTypes = configuration.GetSection(nameof(TestClusterOptions.ClientBuilderConfiguratorTypes))?.Get<string[]>();
            if (builderConfiguratorTypes == null) return;

            foreach (var builderConfiguratorType in builderConfiguratorTypes)
            {
                if (!string.IsNullOrWhiteSpace(builderConfiguratorType))
                {
                    var builderConfigurator = (IClientBuilderConfigurator)Activator.CreateInstance(Type.GetType(builderConfiguratorType, true));
                    builderConfigurator.Configure(configuration, clientBuilder);
                }
            }
        }

        private static void TryConfigureTestClusterMembership(HostBuilderContext context, IServiceCollection services)
        {
            bool.TryParse(context.Configuration[nameof(TestClusterOptions.UseTestClusterMembership)], out bool useTestClusterMembership);

            // Configure test cluster membership if requested and if no membership table implementation has been registered.
            // If the test involves a custom membership oracle and no membership table, special care will be required
            if (useTestClusterMembership && services.All(svc => svc.ServiceType != typeof(IMembershipTable)))
            {
                var primarySiloEndPoint = new IPEndPoint(IPAddress.Loopback, int.Parse(context.Configuration["PrimarySiloPort"]));

                services.UseDevelopmentMembership(options => options.PrimarySiloEndpoint = primarySiloEndPoint);
            }
        }

        private static void TryConfigureTestClusterMembership(IConfiguration configuration, IServiceCollection services)
        {
            bool.TryParse(configuration[nameof(TestClusterOptions.UseTestClusterMembership)], out bool useTestClusterMembership);
            if (useTestClusterMembership && services.All(svc => svc.ServiceType != typeof(IGatewayListProvider)))
            {
                services.UseStaticGatewayListProvider(options =>
                {
                    int baseGatewayPort = int.Parse(configuration["BaseGatewayPort"]);
                    int initialSilosCount = int.Parse(configuration["InitialSilosCount"]);

                    options.Gateways = Enumerable.Range(baseGatewayPort, initialSilosCount)
                        .Select(port => new IPEndPoint(IPAddress.Loopback, port).ToGatewayUri())
                        .ToList();
                });
            }
        }


        private static void TryConfigureFileLogging(IConfiguration configuration, IServiceCollection services, string name)
        {
            bool.TryParse(configuration["UseTestClusterMemebership"], out bool configureFileLogging);
            if (configureFileLogging)
            {
                var fileName = TestingUtils.CreateTraceFileName(name, configuration["ClusterId"]);
                services.AddLogging(loggingBuilder => loggingBuilder.AddFile(fileName));
            }
        }

        private static void AddDefaultApplicationParts(IApplicationPartManager applicationPartsManager)
        {
            var hasApplicationParts = applicationPartsManager.ApplicationParts.OfType<AssemblyPart>()
                .Any(part => !part.IsFrameworkAssembly);
            if (!hasApplicationParts)
            {
                applicationPartsManager.AddFromAppDomain();
                applicationPartsManager.AddFromApplicationBaseDirectory();
            }
        }

        private static void InitializeTestHooksSystemTarget(ISiloHost host)
        {
            var testHook = host.Services.GetRequiredService<TestHooksSystemTarget>();
            var providerRuntime = host.Services.GetRequiredService<SiloProviderRuntime>();
            providerRuntime.RegisterSystemTarget(testHook);
        }
    }
}
