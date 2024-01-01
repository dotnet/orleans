using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;

namespace Orleans.Runtime
{
    /// <summary>
    /// The central point for creating grain contexts.
    /// </summary>
    public sealed class GrainContextActivator
    {
        private readonly object _lockObj = new object();
        private readonly IGrainContextActivatorProvider[] _activatorProviders;
        private readonly IConfigureGrainContextProvider[] _configuratorProviders;
        private readonly GrainPropertiesResolver _resolver;
        private ImmutableDictionary<GrainType, (IGrainContextActivator Activator, IConfigureGrainContext[] ConfigureActions)> _activators
            = ImmutableDictionary<GrainType, (IGrainContextActivator, IConfigureGrainContext[])>.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainContextActivator"/> class.
        /// </summary>
        /// <param name="providers">The grain context activator providers.</param>
        /// <param name="configureContextActions">The <see cref="IConfigureGrainContext"/> providers.</param>
        /// <param name="grainPropertiesResolver">The grain properties resolver.</param>
        public GrainContextActivator(
            IEnumerable<IGrainContextActivatorProvider> providers,
            IEnumerable<IConfigureGrainContextProvider> configureContextActions,
            GrainPropertiesResolver grainPropertiesResolver)
        {
            _resolver = grainPropertiesResolver;
            _activatorProviders = providers.ToArray();
            _configuratorProviders = configureContextActions.ToArray();
        }

        /// <summary>
        /// Creates a new grain context for the provided grain address.
        /// </summary>
        /// <param name="address">The grain address.</param>
        /// <returns>The grain context.</returns>
        public IGrainContext CreateInstance(GrainAddress address)
        {
            var grainId = address.GrainId;
            if (!_activators.TryGetValue(grainId.Type, out var activator))
            {
                activator = this.CreateActivator(grainId.Type);
            }

            var result = activator.Activator.CreateContext(address);
            foreach (var configure in activator.ConfigureActions)
            {
                configure.Configure(result);
            }

            return result;
        }

        private (IGrainContextActivator, IConfigureGrainContext[]) CreateActivator(GrainType grainType)
        {
            lock (_lockObj)
            {
                if (!_activators.TryGetValue(grainType, out var configuredActivator))
                {
                    IGrainContextActivator unconfiguredActivator = null;
                    foreach (var provider in this._activatorProviders)
                    {
                        if (provider.TryGet(grainType, out unconfiguredActivator))
                        {
                            break;
                        }
                    }

                    if (unconfiguredActivator is null)
                    {
                        throw new InvalidOperationException($"Unable to find an {nameof(IGrainContextActivatorProvider)} for grain type {grainType}");
                    }

                    var properties = _resolver.GetGrainProperties(grainType);
                    List<IConfigureGrainContext> configureActions = new List<IConfigureGrainContext>();
                    foreach (var provider in _configuratorProviders)
                    {
                        if (provider.TryGetConfigurator(grainType, properties, out var configurator))
                        {
                            configureActions.Add(configurator);
                        }
                    }

                    configuredActivator = (unconfiguredActivator, configureActions.ToArray());
                    _activators = _activators.SetItem(grainType, configuredActivator);
                }

                return configuredActivator;
            }
        }
    }

    /// <summary>
    /// Provides a <see cref="IGrainContextActivator"/> for a specified grain type.
    /// </summary>
    public interface IGrainContextActivatorProvider
    {
        /// <summary>
        /// Returns a grain context activator for the given grain type.
        /// </summary>
        /// <param name="grainType">Type of the grain.</param>
        /// <param name="activator">The grain context activator.</param>
        /// <returns><see langword="true"/> if an appropriate activator was found, otherwise <see langword="false"/>.</returns>
        bool TryGet(GrainType grainType, [NotNullWhen(true)] out IGrainContextActivator activator);
    }

    /// <summary>
    /// Creates a grain context for the given grain address.
    /// </summary>
    public interface IGrainContextActivator
    {
        /// <summary>
        /// Creates a grain context for the given grain address.
        /// </summary>
        /// <param name="address">The grain address.</param>
        /// <returns>The newly created grain context.</returns>
        public IGrainContext CreateContext(GrainAddress address);
    }

    /// <summary>
    /// Provides a <see cref="IConfigureGrainContext"/> instance for the provided grain type.
    /// </summary>
    public interface IConfigureGrainContextProvider
    {
        /// <summary>
        /// Provides a <see cref="IConfigureGrainContext" /> instance for the provided grain type.
        /// </summary>
        /// <param name="grainType">Type of the grain.</param>
        /// <param name="properties">The grain properties.</param>
        /// <param name="configurator">The configuration provider.</param>
        /// <returns><see langword="true"/> if a configuration provider was found, <see langword="false"/> otherwise.</returns>
        bool TryGetConfigurator(GrainType grainType, GrainProperties properties, [NotNullWhen(true)] out IConfigureGrainContext configurator);
    }

    /// <summary>
    /// Configures the provided grain context.
    /// </summary>
    public interface IConfigureGrainContext
    {
        /// <summary>
        /// Configures the provided grain context.
        /// </summary>
        /// <param name="context">The grain context.</param>
        void Configure(IGrainContext context);
    }

    /// <summary>
    /// Resolves components which are common to all instances of a given grain type.
    /// </summary>
    public class GrainTypeSharedContextResolver
    {
        private readonly ConcurrentDictionary<GrainType, GrainTypeSharedContext> _components = new();
        private readonly IConfigureGrainTypeComponents[] _configurators;
        private readonly GrainPropertiesResolver _grainPropertiesResolver;
        private readonly GrainReferenceActivator _grainReferenceActivator;
        private readonly Func<GrainType, GrainTypeSharedContext> _createFunc;
        private readonly IClusterManifestProvider _clusterManifestProvider;
        private readonly GrainClassMap _grainClassMap;
        private readonly IOptions<SiloMessagingOptions> _messagingOptions;
        private readonly IOptions<GrainCollectionOptions> _collectionOptions;
        private readonly IOptions<SchedulingOptions> _schedulingOptions;
        private readonly PlacementStrategyResolver _placementStrategyResolver;
        private readonly IGrainRuntime _grainRuntime;
        private readonly ILogger<Grain> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly SerializerSessionPool _serializerSessionPool;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainTypeSharedContextResolver"/> class.
        /// </summary>
        /// <param name="configurators">The grain type component configuration providers.</param>
        /// <param name="grainPropertiesResolver">The grain properties resolver.</param>
        /// <param name="grainReferenceActivator">The grain reference activator.</param>
        /// <param name="clusterManifestProvider">The cluster manifest provider.</param>
        /// <param name="grainClassMap">The grain class map.</param>
        /// <param name="placementStrategyResolver">The grain placement strategy resolver.</param>
        /// <param name="messagingOptions">The messaging options.</param>
        /// <param name="collectionOptions">The grain activation collection options</param>
        /// <param name="schedulingOptions">The scheduling options</param>
        /// <param name="grainRuntime">The grain runtime.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="serializerSessionPool">The serializer session pool.</param>
        public GrainTypeSharedContextResolver(
            IEnumerable<IConfigureGrainTypeComponents> configurators,
            GrainPropertiesResolver grainPropertiesResolver,
            GrainReferenceActivator grainReferenceActivator,
            IClusterManifestProvider clusterManifestProvider,
            GrainClassMap grainClassMap,
            PlacementStrategyResolver placementStrategyResolver,
            IOptions<SiloMessagingOptions> messagingOptions,
            IOptions<GrainCollectionOptions> collectionOptions,
            IOptions<SchedulingOptions> schedulingOptions,
            IGrainRuntime grainRuntime,
            ILogger<Grain> logger,
            IServiceProvider serviceProvider,
            SerializerSessionPool serializerSessionPool)
        {
            _configurators = configurators.ToArray();
            _grainPropertiesResolver = grainPropertiesResolver;
            _grainReferenceActivator = grainReferenceActivator;
            _clusterManifestProvider = clusterManifestProvider;
            _grainClassMap = grainClassMap;
            _placementStrategyResolver = placementStrategyResolver;
            _messagingOptions = messagingOptions;
            _collectionOptions = collectionOptions;
            _schedulingOptions = schedulingOptions;
            _grainRuntime = grainRuntime;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _serializerSessionPool = serializerSessionPool;
            _createFunc = Create;
        }

        /// <summary>
        /// Returns shared grain components for the provided grain type.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <returns>The shared context for all grains of the provided type.</returns>
        public GrainTypeSharedContext GetComponents(GrainType grainType) => _components.GetOrAdd(grainType, _createFunc);

        private GrainTypeSharedContext Create(GrainType grainType)
        {
            var result = new GrainTypeSharedContext(
                grainType,
                _clusterManifestProvider,
                _grainClassMap,
                _placementStrategyResolver,
                _messagingOptions,
                _collectionOptions,
                _schedulingOptions,
                _grainRuntime,
                _logger,
                _grainReferenceActivator,
                _serviceProvider,
                _serializerSessionPool);
            var properties = _grainPropertiesResolver.GetGrainProperties(grainType);
            foreach (var configurator in _configurators)
            {
                configurator.Configure(grainType, properties, result);
            }

            return result;
        }
    }

    /// <summary>
    /// Configures shared components which are common for all instances of a given grain type.
    /// </summary>
    public interface IConfigureGrainTypeComponents
    {
        /// <summary>
        /// Configures shared components which are common for all instances of a given grain type.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="properties">The grain properties.</param>
        /// <param name="shared">The shared context for all grains of the specified type.</param>
        void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared);
    }

    internal class ReentrantSharedComponentsConfigurator : IConfigureGrainTypeComponents
    {
        public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
        {
            if (properties.Properties.TryGetValue(WellKnownGrainTypeProperties.Reentrant, out var value) && bool.Parse(value))
            {
                var component = shared.GetComponent<GrainCanInterleave>();
                if (component is null)
                {
                    component = new GrainCanInterleave();
                    shared.SetComponent<GrainCanInterleave>(component);
                }

                component.MayInterleavePredicates.Add(ReentrantPredicate.Instance);
            }
        }
    }

    internal class MayInterleaveConfiguratorProvider : IConfigureGrainContextProvider
    {
        private readonly GrainClassMap _grainClassMap;

        public MayInterleaveConfiguratorProvider(GrainClassMap grainClassMap)
        {
            _grainClassMap = grainClassMap;
        }

        public bool TryGetConfigurator(GrainType grainType, GrainProperties properties, out IConfigureGrainContext configurator)
        {
            if (properties.Properties.TryGetValue(WellKnownGrainTypeProperties.MayInterleavePredicate, out _)
                && _grainClassMap.TryGetGrainClass(grainType, out var grainClass))
            {
                var predicate = GetMayInterleavePredicate(grainClass);
                configurator = new MayInterleaveConfigurator(predicate);
                return true;
            }

            configurator = null;
            return false;
        }

        /// <summary>
        /// Returns interleave predicate depending on whether class is marked with <see cref="MayInterleaveAttribute"/> or not.
        /// </summary>
        /// <param name="grainType">Grain class.</param>
        private static IMayInterleavePredicate GetMayInterleavePredicate(Type grainType)
        {
            var attribute = grainType.GetCustomAttribute<MayInterleaveAttribute>();
            if (attribute is null)
            {
                return null;
            }

            // here
            var callbackMethodName = attribute.CallbackMethodName;
            var method = grainType.GetMethod(callbackMethodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (method == null)
            {
                throw new InvalidOperationException(
                    $"Class {grainType.FullName} doesn't declare public method " +
                    $"with name {callbackMethodName} specified in MayInterleave attribute");
            }

            if (method.ReturnType != typeof(bool) ||
                method.GetParameters().Length != 1 ||
                method.GetParameters()[0].ParameterType != typeof(IInvokable))
            {
                throw new InvalidOperationException(
                    $"Wrong signature of callback method {callbackMethodName} " +
                    $"specified in MayInterleave attribute for grain class {grainType.FullName}. \n" +
                    $"Expected: public bool {callbackMethodName}(IInvokable req)");
            }

            if (method.IsStatic)
            {
                return new MayInterleaveStaticPredicate(method.CreateDelegate<Func<IInvokable, bool>>());
            }

            var predicateType = typeof(MayInterleaveInstancedPredicate<>).MakeGenericType(grainType);
            return (IMayInterleavePredicate)Activator.CreateInstance(predicateType, method);
        }
    }

    internal interface IMayInterleavePredicate
    {
        bool Invoke(object instance, IInvokable bodyObject);
    }

    internal class ReentrantPredicate : IMayInterleavePredicate
    {
        private ReentrantPredicate()
        {
        }

        public static ReentrantPredicate Instance { get; } = new();

        public bool Invoke(object _, IInvokable bodyObject) => true;
    }

    internal class MayInterleaveStaticPredicate : IMayInterleavePredicate
    {
        private readonly Func<IInvokable, bool> _mayInterleavePredicate;

        public MayInterleaveStaticPredicate(Func<IInvokable, bool> mayInterleavePredicate)
        {
            _mayInterleavePredicate = mayInterleavePredicate;
        }

        public bool Invoke(object _, IInvokable bodyObject) => _mayInterleavePredicate(bodyObject);
    }

    internal class MayInterleaveInstancedPredicate<T> : IMayInterleavePredicate where T : class
    {
        private readonly Func<T, IInvokable, bool> _mayInterleavePredicate;

        public MayInterleaveInstancedPredicate(MethodInfo mayInterleavePredicateInfo)
        {
            _mayInterleavePredicate = mayInterleavePredicateInfo.CreateDelegate<Func<T, IInvokable, bool>>();
        }

        public bool Invoke(object instance, IInvokable bodyObject) => _mayInterleavePredicate(instance as T, bodyObject);
    }

    internal class MayInterleaveConfigurator : IConfigureGrainContext
    {
        private readonly IMayInterleavePredicate _mayInterleavePredicate;

        public MayInterleaveConfigurator(IMayInterleavePredicate mayInterleavePredicate)
        {
            _mayInterleavePredicate = mayInterleavePredicate;
        }

        public void Configure(IGrainContext context)
        {
            var component = context.GetComponent<GrainCanInterleave>();
            if (component is null)
            {
                component = new GrainCanInterleave();
                context.SetComponent<GrainCanInterleave>(component);
            }

            component.MayInterleavePredicates.Add(_mayInterleavePredicate);
        }
    }

    internal class GrainCanInterleave
    {
        public List<IMayInterleavePredicate> MayInterleavePredicates { get; } = new List<IMayInterleavePredicate>();
        public bool MayInterleave(object instance, Message message)
        {
            foreach (var predicate in this.MayInterleavePredicates)
            {
                if (predicate.Invoke(instance, message.BodyObject as IInvokable)) return true;
            }

            return false;
        }
    }
}