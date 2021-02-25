using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Hosting;
using Orleans.Runtime.TestHooks;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Statistics;
using Orleans.TestingHost.Utils;
using Orleans.TestingHost.Logging;
using Orleans.Configuration.Internal;

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
        /// <param name="configuration">The configuration.</param>
        /// <returns>A new silo.</returns>
        public static ISiloHost CreateSiloHost(string hostName, IConfiguration configuration)
        {
            string siloName = configuration[nameof(TestSiloSpecificOptions.SiloName)] ?? hostName;

            var hostBuilder = new HostBuilder();
            var siloBuilder = new SiloBuilder(hostBuilder);
            var siloHostBuilder = new SiloHostBuilderAdaptor(hostBuilder, siloBuilder);

            // Add the silo builder to the host builder so that it is executed during configuration time. 
            hostBuilder.Properties[nameof(SiloBuilder)] = siloBuilder;
            hostBuilder.ConfigureServices((context, services) =>
            {
                siloBuilder.Build(context, services);
            });

            siloBuilder
                .Configure<ClusterOptions>(configuration)
                .Configure<SiloOptions>(options => options.SiloName = siloName);

            hostBuilder.ConfigureHostConfiguration(cb => cb.AddConfiguration(configuration));

            hostBuilder.Properties["Configuration"] = configuration;
            ConfigureAppServices(configuration, hostBuilder, siloBuilder, siloHostBuilder);

            hostBuilder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<TestHooksHostEnvironmentStatistics>();
                services.AddFromExisting<IHostEnvironmentStatistics, TestHooksHostEnvironmentStatistics>();
                services.AddSingleton<TestHooksSystemTarget>();
                ConfigureListeningPorts(context.Configuration, services);

                TryConfigureClusterMembership(context.Configuration, services);
                TryConfigureFileLogging(configuration, services, siloName);

                if (Debugger.IsAttached)
                {
                    // Test is running inside debugger - Make timeout ~= infinite
                    services.Configure<SiloMessagingOptions>(op => op.ResponseTimeout = TimeSpan.FromMilliseconds(1000000));
                }
            });

            var host = hostBuilder.Build();
            var silo = host.Services.GetRequiredService<ISiloHost>();
            InitializeTestHooksSystemTarget(silo);
            return silo;
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

            builder.ConfigureServices(services =>
            {
                TryConfigureClientMembership(configuration, services);
                TryConfigureFileLogging(configuration, services, hostName);
            });

            ConfigureAppServices(configuration, builder);
            return builder.Build();
        }

        public static string SerializeConfiguration(IConfiguration configuration)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                Formatting = Formatting.None,
            };

            KeyValuePair<string, string>[] enumerated = configuration.AsEnumerable().ToArray();
            return JsonConvert.SerializeObject(enumerated, settings);
        }

        public static IConfiguration DeserializeConfiguration(string serializedSources)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
            };

            var builder = new ConfigurationBuilder();
            var enumerated = JsonConvert.DeserializeObject<KeyValuePair<string, string>[]>(serializedSources, settings);
            builder.AddInMemoryCollection(enumerated);
            return builder.Build();
        }

        private static void ConfigureListeningPorts(IConfiguration configuration, IServiceCollection services)
        {
            int siloPort = int.Parse(configuration[nameof(TestSiloSpecificOptions.SiloPort)]);
            int gatewayPort = int.Parse(configuration[nameof(TestSiloSpecificOptions.GatewayPort)]);

            services.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = IPAddress.Loopback;
                options.SiloPort = siloPort;
                options.GatewayPort = gatewayPort;
                options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Loopback, siloPort);
                if (gatewayPort != 0)
                {
                    options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Loopback, gatewayPort);
                }
            });
        }

        private static void ConfigureAppServices(IConfiguration configuration, IHostBuilder hostBuilder, ISiloBuilder siloBuilder, ISiloHostBuilder siloHostBuilder)
        {
            var builderConfiguratorTypes = configuration.GetSection(nameof(TestClusterOptions.SiloBuilderConfiguratorTypes))?.Get<string[]>();
            if (builderConfiguratorTypes == null) return;

            foreach (var builderConfiguratorType in builderConfiguratorTypes)
            {
                if (!string.IsNullOrWhiteSpace(builderConfiguratorType))
                {
                    var configurator = Activator.CreateInstance(Type.GetType(builderConfiguratorType, true));

                    (configurator as IHostConfigurator)?.Configure(hostBuilder);
                    (configurator as ISiloConfigurator)?.Configure(siloBuilder);
#pragma warning disable CS0618 // Type or member is obsolete
                    (configurator as ISiloBuilderConfigurator)?.Configure(siloHostBuilder);
#pragma warning restore CS0618 // Type or member is obsolete
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

        private static void TryConfigureClusterMembership(IConfiguration configuration, IServiceCollection services)
        {
            bool.TryParse(configuration[nameof(TestClusterOptions.UseTestClusterMembership)], out bool useTestClusterMembership);

            // Configure test cluster membership if requested and if no membership table implementation has been registered.
            // If the test involves a custom membership oracle and no membership table, special care will be required
            if (useTestClusterMembership && services.All(svc => svc.ServiceType != typeof(IMembershipTable)))
            {
                var primarySiloEndPoint = new IPEndPoint(IPAddress.Loopback, int.Parse(configuration[nameof(TestSiloSpecificOptions.PrimarySiloPort)]));

                services.Configure<DevelopmentClusterMembershipOptions>(options => options.PrimarySiloEndpoint = primarySiloEndPoint);
                services
                    .AddSingleton<SystemTargetBasedMembershipTable>()
                    .AddFromExisting<IMembershipTable, SystemTargetBasedMembershipTable>();
            }
        }

        private static void TryConfigureClientMembership(IConfiguration configuration, IServiceCollection services)
        {
            bool.TryParse(configuration[nameof(TestClusterOptions.UseTestClusterMembership)], out bool useTestClusterMembership);
            if (useTestClusterMembership && services.All(svc => svc.ServiceType != typeof(IGatewayListProvider)))
            {
                Action<StaticGatewayListProviderOptions> configureOptions = options =>
                    {
                        int baseGatewayPort = int.Parse(configuration[nameof(TestClusterOptions.BaseGatewayPort)]);
                        int initialSilosCount = int.Parse(configuration[nameof(TestClusterOptions.InitialSilosCount)]);
                        bool gatewayPerSilo = bool.Parse(configuration[nameof(TestClusterOptions.GatewayPerSilo)]);

                        if (gatewayPerSilo)
                        {
                            options.Gateways = Enumerable.Range(baseGatewayPort, initialSilosCount)
                                .Select(port => new IPEndPoint(IPAddress.Loopback, port).ToGatewayUri())
                                .ToList();
                        }
                        else
                        {
                            options.Gateways = new List<Uri> { new IPEndPoint(IPAddress.Loopback, baseGatewayPort).ToGatewayUri() };
                        }
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
            var catalog = host.Services.GetRequiredService<Catalog>();
            catalog.RegisterSystemTarget(testHook);
        }
    }
}
