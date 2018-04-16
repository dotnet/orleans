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
using Orleans.Runtime.MembershipService;
using Orleans.Statistics;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Utility for creating silos given a name and collection of configuration sources.
    /// </summary>
    public class TestClusterHostFactory
    {
        /// <summary>
        /// Creates an returns a new silo.
        /// </summary>
        /// <param name="hostName">The silo name if it is not already specified in the configuration.</param>
        /// <param name="configurationSources">The configuration.</param>
        /// <returns>A new silo.</returns>
        public static ISiloHost CreateSiloHost(string hostName, IEnumerable<IConfigurationSource> configurationSources)
        {
            var configBuilder = new ConfigurationBuilder();
            foreach (var source in configurationSources)
            {
                configBuilder.Add(source);
            }
            var configuration = configBuilder.Build();

            string siloName = configuration[nameof(TestSiloSpecificOptions.SiloName)] ?? hostName;

            var hostBuilder = new SiloHostBuilder()
                .Configure<ClusterOptions>(configuration)
                .Configure<SiloOptions>(options => options.SiloName = siloName)
                .Configure<ClusterMembershipOptions>(options =>
                {
                    options.ExpectedClusterSize = int.Parse(configuration["InitialSilosCount"]);
                })
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
                services.AddSingleton<TestHooksHostEnvironmentStatistics>();
                services.AddFromExisting<IHostEnvironmentStatistics, TestHooksHostEnvironmentStatistics>();
                services.AddSingleton<TestHooksSystemTarget>();
                ConfigureListeningPorts(context, services);

                TryConfigureTestClusterMembership(context, services);
                TryConfigureFileLogging(configuration, services, siloName);

                if (Debugger.IsAttached)
                {
                    // Test is running inside debugger - Make timeout ~= infinite
                    services.Configure<SiloMessagingOptions>(op => op.ResponseTimeout = TimeSpan.FromMilliseconds(1000000));
                }
            });

            hostBuilder.GetApplicationPartManager().ConfigureDefaults();

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
            builder.Configure<ClusterOptions>(configuration);
            ConfigureAppServices(configuration, builder);

            builder.ConfigureServices(services =>
            {
                TryConfigureTestClusterMembership(configuration, services);
                TryConfigureFileLogging(configuration, services, hostName);
            });

            builder.GetApplicationPartManager().ConfigureDefaults();
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
            int siloPort = int.Parse(context.Configuration[nameof(TestSiloSpecificOptions.SiloPort)]);
            int gatewayPort = int.Parse(context.Configuration[nameof(TestSiloSpecificOptions.GatewayPort)]);

            services.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = IPAddress.Loopback;
                options.SiloPort = siloPort;
                options.GatewayPort = gatewayPort;
            });
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
                var primarySiloEndPoint = new IPEndPoint(IPAddress.Loopback, int.Parse(context.Configuration[nameof(TestSiloSpecificOptions.PrimarySiloPort)]));

                services.Configure<DevelopmentClusterMembershipOptions>(options => options.PrimarySiloEndpoint = primarySiloEndPoint);
                services
                    .AddSingleton<SystemTargetBasedMembershipTable>()
                    .AddFromExisting<IMembershipTable, SystemTargetBasedMembershipTable>();
            }
        }

        private static void TryConfigureTestClusterMembership(IConfiguration configuration, IServiceCollection services)
        {
            bool.TryParse(configuration[nameof(TestClusterOptions.UseTestClusterMembership)], out bool useTestClusterMembership);
            if (useTestClusterMembership && services.All(svc => svc.ServiceType != typeof(IGatewayListProvider)))
            {
                Action<StaticGatewayListProviderOptions> configureOptions = options =>
                    {
                        int baseGatewayPort = int.Parse(configuration[nameof(TestClusterOptions.BaseGatewayPort)]);
                        int initialSilosCount = int.Parse(configuration[nameof(TestClusterOptions.InitialSilosCount)]);

                        options.Gateways = Enumerable.Range(baseGatewayPort, initialSilosCount)
                            .Select(port => new IPEndPoint(IPAddress.Loopback, port).ToGatewayUri())
                            .ToList();
                    };
                if (configureOptions != null)
                {
                    services.Configure(configureOptions);
                }

                services.AddSingleton<IGatewayListProvider, StaticGatewayListProvider>()
                    .ConfigureFormatter<StaticGatewayListProviderOptions>();
            }
        }


        private static void TryConfigureFileLogging(IConfiguration configuration, IServiceCollection services, string name)
        {
            bool.TryParse(configuration[nameof(TestClusterOptions.ConfigureFileLogging)], out bool configureFileLogging);
            if (configureFileLogging)
            {
                var fileName = TestingUtils.CreateTraceFileName(name, configuration[nameof(TestClusterOptions.ClusterId)]);
                services.AddLogging(loggingBuilder => loggingBuilder.AddFile(fileName));
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
