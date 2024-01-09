using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.TestHooks;
using Orleans.Statistics;
using Orleans.TestingHost.Logging;
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
        /// <param name="configuration">The configuration.</param>
        /// <param name="postConfigureHostBuilder">An optional delegate which can be used to configure the host builder just prior to a host being built.</param>
        /// <returns>A new silo.</returns>
        public static IHost CreateSiloHost(string hostName, IConfiguration configuration, Action<IHostBuilder> postConfigureHostBuilder = null)
        {
            string siloName = configuration["Name"] ?? hostName;

            var hostBuilder = new HostBuilder();
            hostBuilder.UseEnvironment(Environments.Development);
            hostBuilder.Properties["Configuration"] = configuration;
            hostBuilder.ConfigureHostConfiguration(cb => cb.AddConfiguration(configuration));

            hostBuilder.UseOrleans((ctx, siloBuilder) =>
            {
                siloBuilder.Services
                    .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
            });

            ConfigureAppServices(configuration, hostBuilder);

            hostBuilder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<TestHooksHostEnvironmentStatistics>();
                services.AddFromExisting<IHostEnvironmentStatistics, TestHooksHostEnvironmentStatistics>();
                services.AddSingleton<TestHooksSystemTarget>();

                //TryConfigureClusterMembership(context.Configuration, services);
                TryConfigureFileLogging(configuration, services, siloName);

                if (Debugger.IsAttached)
                {
                    // Test is running inside debugger - Make timeout ~= infinite
                    services.Configure<SiloMessagingOptions>(op => op.ResponseTimeout = TimeSpan.FromMilliseconds(1000000));
                }
            });

            postConfigureHostBuilder?.Invoke(hostBuilder);
            var host = hostBuilder.Build();
            InitializeTestHooksSystemTarget(host);
            return host;
        }

        /// <summary>
        /// Creates the cluster client.
        /// </summary>
        /// <param name="hostName">Name of the host.</param>
        /// <param name="configuration">The configuration.</param>
        /// <param name="postConfigureHostBuilder">An optional delegate which can be used to configure the host builder just prior to a host being built.</param>
        /// <returns>The cluster client host.</returns>
        public static IHost CreateClusterClient(string hostName, IConfiguration configuration, Action<IHostBuilder> postConfigureHostBuilder = null)
        {
            var hostBuilder = new HostBuilder();
            hostBuilder.UseEnvironment(Environments.Development);
            hostBuilder.Properties["Configuration"] = configuration;
            hostBuilder.ConfigureHostConfiguration(cb => cb.AddConfiguration(configuration));

            hostBuilder.UseOrleansClient();

            ConfigureClientAppServices(configuration, hostBuilder);

            hostBuilder
                .ConfigureServices(services =>
                {
                    //TryConfigureClientMembership(configuration, services);
                    TryConfigureFileLogging(configuration, services, hostName);
                });

            postConfigureHostBuilder?.Invoke(hostBuilder);
            var host = hostBuilder.Build();

            return host;
        }

        /// <summary>
        /// Serializes configuration to a string.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <returns>The serialized configuration.</returns>
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

        /// <summary>
        /// Deserializes a configuration string.
        /// </summary>
        /// <param name="serializedSources">The serialized sources.</param>
        /// <returns>The deserialized configuration.</returns>
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

        private static void ConfigureAppServices(IConfiguration configuration, IHostBuilder hostBuilder)
        {
            var builderConfiguratorTypes = configuration.GetSection(nameof(TestClusterOptions.SiloBuilderConfiguratorTypes))?.Get<string[]>();
            if (builderConfiguratorTypes == null) return;

            foreach (var builderConfiguratorType in builderConfiguratorTypes)
            {
                if (!string.IsNullOrWhiteSpace(builderConfiguratorType))
                {
                    var configurator = Activator.CreateInstance(Type.GetType(builderConfiguratorType, true));

                    (configurator as IHostConfigurator)?.Configure(hostBuilder);
                    hostBuilder.UseOrleans((ctx, siloBuilder) => (configurator as ISiloConfigurator)?.Configure(siloBuilder));
                }
            }
        }

        private static void ConfigureClientAppServices(IConfiguration configuration, IHostBuilder hostBuilder)
        {
            var builderConfiguratorTypes = configuration.GetSection(nameof(TestClusterOptions.ClientBuilderConfiguratorTypes))?.Get<string[]>();
            if (builderConfiguratorTypes == null) return;

            foreach (var builderConfiguratorType in builderConfiguratorTypes)
            {
                if (!string.IsNullOrWhiteSpace(builderConfiguratorType))
                {
                    var builderConfigurator = Activator.CreateInstance(Type.GetType(builderConfiguratorType, true));

                    (builderConfigurator as IHostConfigurator)?.Configure(hostBuilder);

                    if (builderConfigurator is IClientBuilderConfigurator clientBuilderConfigurator)
                    {
                        hostBuilder.UseOrleansClient(clientBuilder => clientBuilderConfigurator.Configure(configuration, clientBuilder));
                    }
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
                var primarySiloEndPoint = new IPEndPoint(IPAddress.Loopback, int.Parse(configuration[nameof(TestSiloSpecificOptions.PrimarySiloEndPoint)]));

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

                services.Configure(configureOptions);

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

        private static void InitializeTestHooksSystemTarget(IHost host)
        {
            var testHook = host.Services.GetRequiredService<TestHooksSystemTarget>();
            var catalog = host.Services.GetRequiredService<Catalog>();
            catalog.RegisterSystemTarget(testHook);
        }
    }
}
