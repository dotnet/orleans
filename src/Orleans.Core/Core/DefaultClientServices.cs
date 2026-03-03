#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Configuration.Validators;
using Orleans.GrainReferences;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Networking.Shared;
using Orleans.Placement.Repartitioning;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Versions;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Internal;
using Orleans.Serialization.Serializers;
using Orleans.Statistics;

namespace Orleans
{
    /// <summary>
    /// Configures the default services for a client.
    /// </summary>
    internal static class DefaultClientServices
    {
        private static readonly ServiceDescriptor ServiceDescriptor = new(typeof(ServicesAdded), new ServicesAdded());

        /// <summary>
        /// Configures the default services for a client.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        public static void AddDefaultServices(IClientBuilder builder)
        {
            var services = builder.Services;
            if (services.Contains(ServiceDescriptor))
            {
                return;
            }

            services.Add(ServiceDescriptor);

            // Common services
            services.AddLogging();
            services.AddOptions();
            services.AddMetrics();
            services.TryAddSingleton<TimeProvider>(TimeProvider.System);
            services.TryAddSingleton<OrleansInstruments>();

            // Options logging
            services.TryAddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
            services.TryAddSingleton(typeof(IOptionFormatterResolver<>), typeof(DefaultOptionsFormatterResolver<>));

            services.AddSingleton<ClientOptionsLogger>();
            services.AddFromExisting<ILifecycleParticipant<IClusterClientLifecycle>, ClientOptionsLogger>();

            // Lifecycle
            services.AddSingleton<ServiceLifecycle<IClusterClientLifecycle>>();
            services.TryAddFromExisting<IServiceLifecycle, ServiceLifecycle<IClusterClientLifecycle>>();
            services.AddFromExisting<ILifecycleParticipant<IClusterClientLifecycle>, ServiceLifecycle<IClusterClientLifecycle>>();

            // Statistics
            services.AddSingleton<IEnvironmentStatisticsProvider, EnvironmentStatisticsProvider>();
#pragma warning disable 618
            services.AddSingleton<OldEnvironmentStatistics>();
            services.AddFromExisting<IAppEnvironmentStatistics, OldEnvironmentStatistics>();
            services.AddFromExisting<IHostEnvironmentStatistics, OldEnvironmentStatistics>();
#pragma warning restore 618

            services.TryAddSingleton<GrainBindingsResolver>();
            services.TryAddSingleton<LocalClientDetails>();
            services.TryAddSingleton<OutsideRuntimeClient>();
            services.TryAddSingleton<InterfaceToImplementationMappingCache>();
            services.TryAddSingleton<ClientGrainContext>();
            services.AddSingleton<IClusterConnectionStatusObserver, ClusterConnectionStatusObserverAdaptor>();
            services.AddFromExisting<IGrainContextAccessor, ClientGrainContext>();
            services.TryAddFromExisting<IRuntimeClient, OutsideRuntimeClient>();
            services.TryAddFromExisting<IClusterConnectionStatusListener, OutsideRuntimeClient>();
            services.TryAddSingleton<GrainFactory>();
            services.TryAddSingleton<GrainInterfaceTypeToGrainTypeResolver>();
            services.TryAddSingleton<GrainReferenceActivator>();
            services.AddSingleton<IGrainReferenceActivatorProvider, GrainReferenceActivatorProvider>();
            services.AddSingleton<IGrainReferenceActivatorProvider, UntypedGrainReferenceActivatorProvider>();
            services.TryAddSingleton<RpcProvider>();
            services.TryAddSingleton<IGrainReferenceRuntime, GrainReferenceRuntime>();
            services.TryAddSingleton<GrainPropertiesResolver>();
            services.TryAddSingleton<IGrainCancellationTokenRuntime, GrainCancellationTokenRuntime>();
            services.TryAddFromExisting<IGrainFactory, GrainFactory>();
            services.TryAddFromExisting<IInternalGrainFactory, GrainFactory>();
            services.TryAddSingleton<ClientProviderRuntime>();
            services.TryAddSingleton<MessageFactory>();
            services.TryAddFromExisting<IProviderRuntime, ClientProviderRuntime>();
            services.TryAddSingleton<ClusterClient>();
            services.TryAddFromExisting<IClusterClient, ClusterClient>();
            services.TryAddFromExisting<IInternalClusterClient, ClusterClient>();
            services.AddFromExisting<IHostedService, ClusterClient>();
            services.AddTransient<IOptions<MessagingOptions>>(static sp => sp.GetRequiredService<IOptions<ClientMessagingOptions>>());
            services.TryAddSingleton<IClientConnectionRetryFilter, LinearBackoffClientConnectionRetryFilter>();

            services.AddSingleton<IConfigureOptions<GrainTypeOptions>, DefaultGrainTypeOptionsProvider>();

            // Add default option formatter if none is configured, for options which are required to be configured
            services.ConfigureFormatter<ClusterOptions>();
            services.ConfigureFormatter<ClientMessagingOptions>();
            services.ConfigureFormatter<ConnectionOptions>();

            services.AddTransient<IConfigurationValidator, GrainTypeOptionsValidator>();
            services.AddTransient<IConfigurationValidator, ClusterOptionsValidator>();
            services.AddTransient<IConfigurationValidator, ClientClusteringValidator>();
            services.AddTransient<IConfigurationValidator, SerializerConfigurationValidator>();

            // TODO: abstract or move into some options.
            services.AddSingleton<SocketSchedulers>();
            services.AddSingleton<SharedMemoryPool>();

            // Networking
            services.TryAddSingleton<IMessageStatisticsSink, NoOpMessageStatisticsSink>();
            services.TryAddSingleton<ConnectionCommon>();
            services.TryAddSingleton<ConnectionManager>();
            services.TryAddSingleton<ConnectionPreambleHelper>();
            services.AddSingleton<ILifecycleParticipant<IClusterClientLifecycle>, ConnectionManagerLifecycleAdapter<IClusterClientLifecycle>>();

            services.AddKeyedSingleton<IConnectionFactory>(
                ClientOutboundConnectionFactory.ServicesKey,
                (sp, key) => ActivatorUtilities.CreateInstance<SocketConnectionFactory>(sp));

            services.AddSerializer();
            services.AddSingleton<ITypeNameFilter, AllowOrleansTypes>();
            services.AddSingleton<ISpecializableCodec, GrainReferenceCodecProvider>();
            services.AddSingleton<ISpecializableCopier, GrainReferenceCopierProvider>();
            services.AddSingleton<OnDeserializedCallbacks>();
            services.AddSingleton<IPostConfigureOptions<OrleansJsonSerializerOptions>, ConfigureOrleansJsonSerializerOptions>();
            services.AddSingleton<OrleansJsonSerializer>();

            services.TryAddTransient(sp => ActivatorUtilities.CreateInstance<MessageSerializer>(
                sp,
                sp.GetRequiredService<IOptions<ClientMessagingOptions>>().Value));
            services.TryAddSingleton<ConnectionFactory, ClientOutboundConnectionFactory>();
            services.TryAddSingleton<ClientMessageCenter>(sp => sp.GetRequiredService<OutsideRuntimeClient>().MessageCenter);
            services.TryAddFromExisting<IMessageCenter, ClientMessageCenter>();
            services.AddSingleton<GatewayManager>();
            services.AddSingleton<NetworkingTrace>();
            services.AddSingleton<MessagingTrace>();

            // Type metadata
            services.AddSingleton<ClientClusterManifestProvider>();
            services.AddFromExisting<IClusterManifestProvider, ClientClusterManifestProvider>();
            services.AddSingleton<ClientManifestProvider>();
            services.AddSingleton<IGrainInterfaceTypeProvider, AttributeGrainInterfaceTypeProvider>();
            services.AddSingleton<GrainTypeResolver>();
            services.AddSingleton<IGrainTypeProvider, AttributeGrainTypeProvider>();
            services.AddSingleton<GrainPropertiesResolver>();
            services.AddSingleton<GrainVersionManifest>();
            services.AddSingleton<GrainInterfaceTypeResolver>();
            services.AddSingleton<IGrainInterfacePropertiesProvider, AttributeGrainInterfacePropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, AttributeGrainPropertiesProvider>();
            services.AddSingleton<IGrainInterfacePropertiesProvider, TypeNameGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, TypeNameGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, ImplementedInterfaceProvider>();

            services.AddSingleton<IGrainCallCancellationManager, ExternalClientGrainCallCancellationManager>();
            services.AddSingleton<ILocalActivationStatusChecker, ClientLocalActivationStatusChecker>();

            ApplyConfiguration(builder);
        }

        private static void ApplyConfiguration(IClientBuilder builder)
        {
            var services = builder.Services;
            var cfg = builder.Configuration.GetSection("Orleans");
            var knownProviderTypes = GetRegisteredProviders();

            services.Configure<ClusterOptions>(cfg);
            services.Configure<ClientMessagingOptions>(cfg.GetSection("Messaging"));
            services.Configure<GatewayOptions>(cfg.GetSection("Gateway"));

            if (bool.TryParse(cfg["EnableDistributedTracing"], out var enableDistributedTracing) && enableDistributedTracing)
            {
                builder.AddActivityPropagation();
            }

            ApplySubsection(builder, cfg, knownProviderTypes, "Clustering");
            ApplySubsection(builder, cfg, knownProviderTypes, "Reminders");
            ApplyNamedSubsections(builder, cfg, knownProviderTypes, "BroadcastChannel");
            ApplyNamedSubsections(builder, cfg, knownProviderTypes, "Streaming");
            ApplyNamedSubsections(builder, cfg, knownProviderTypes, "GrainStorage");
            ApplyNamedSubsections(builder, cfg, knownProviderTypes, "GrainDirectory");

            static void ConfigureProvider(
                IClientBuilder builder,
                Dictionary<(string Kind, string Name), Type> knownProviderTypes,
                string kind,
                string? name,
                IConfigurationSection configurationSection)
            {
                var providerType = configurationSection["ProviderType"] ?? "Default";
                var provider = GetRequiredProvider(knownProviderTypes, kind, providerType);
                provider.Configure(builder, name, configurationSection);
            }

            static IProviderBuilder<IClientBuilder> GetRequiredProvider(Dictionary<(string Kind, string Name), Type> knownProviderTypes, string kind, string name)
            {
                if (knownProviderTypes.TryGetValue((kind, name), out var type))
                {
                    var instance = Activator.CreateInstance(type);
                    return instance as IProviderBuilder<IClientBuilder>
                        ?? throw new InvalidOperationException($"{kind} provider, '{name}', of type {type}, does not implement {typeof(IProviderBuilder<IClientBuilder>)}.");
                }

                var knownProvidersOfKind = knownProviderTypes
                    .Where(kvp => string.Equals(kvp.Key.Kind, kind, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key.Name)
                    .OrderBy(n => n)
                    .ToList();

                var knownProvidersMessage = knownProvidersOfKind.Count > 0
                    ? $" Known {kind} providers: {string.Join(", ", knownProvidersOfKind)}."
                    : string.Empty;

                throw new InvalidOperationException($"Could not find {kind} provider named '{name}'. This can indicate that either the 'Microsoft.Orleans.Sdk' or the provider's package are not referenced by your application.{knownProvidersMessage}");
            }

            static Dictionary<(string Kind, string Name), Type> GetRegisteredProviders()
            {
                var result = new Dictionary<(string, string), Type>();
                var options = new Orleans.Serialization.Configuration.TypeManifestOptions();
                
                // Collect providers from generated metadata
                foreach (var asm in ReferencedAssemblyProvider.GetRelevantAssemblies())
                {
                    var attrs = asm.GetCustomAttributes<Orleans.Serialization.Configuration.TypeManifestProviderAttribute>();
                    foreach (var attr in attrs)
                    {
                        if (Activator.CreateInstance(attr.ProviderType) is Orleans.Serialization.Configuration.ITypeManifestProvider provider)
                        {
                            provider.Configure(options);
                        }
                    }
                }
                
                // Extract client providers from the collected metadata
                foreach (var kvp in options.RegisteredProviders)
                {
                    if (string.Equals(kvp.Key.Target, "Client", StringComparison.Ordinal))
                    {
                        result[(kvp.Key.Kind, kvp.Key.Name)] = kvp.Value;
                    }
                }
                
                return result;
            }

            static void ApplySubsection(IClientBuilder builder, IConfigurationSection cfg, Dictionary<(string Kind, string Name), Type> knownProviderTypes, string sectionName)
            {
                if (cfg.GetSection(sectionName) is { } section && section.Exists())
                {
                    ConfigureProvider(builder, knownProviderTypes, sectionName, name: null, section);
                }
            }

            static void ApplyNamedSubsections(IClientBuilder builder, IConfigurationSection cfg, Dictionary<(string Kind, string Name), Type> knownProviderTypes, string sectionName)
            {
                if (cfg.GetSection(sectionName) is { } section && section.Exists())
                {
                    foreach (var child in section.GetChildren())
                    {
                        ConfigureProvider(builder, knownProviderTypes, sectionName, name: child.Key, child);
                    }
                }
            }
        }

        internal partial class RootConfiguration
        {
            public IConfigurationSection? Clustering { get; set; }
        }

        /// <summary>
        /// A <see cref="ITypeNameFilter"/> which allows any type from an assembly containing "Orleans" in its name to be allowed for the purposes of serialization and deserialization.
        /// </summary>
        private class AllowOrleansTypes : ITypeNameFilter
        {
            /// <inheritdoc />
            public bool? IsTypeNameAllowed(string typeName, string assemblyName)
            {
                if (assemblyName is { Length: > 0} && assemblyName.Contains("Orleans"))
                {
                    return true;
                }

                return null;
            }
        }

        /// <summary>
        /// A marker type used to determine
        /// </summary>
        private class ServicesAdded { }
    }
}
