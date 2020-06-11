using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Metadata;

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
        private ImmutableDictionary<GrainType, ActivatorEntry> _activators = ImmutableDictionary<GrainType, ActivatorEntry>.Empty;

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
        public IGrainContext CreateInstance(ActivationAddress address)
        {
            var grainId = address.Grain;
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

        private ActivatorEntry CreateActivator(GrainType grainType)
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

                    var applicableConfigureActions = configureActions.Count > 0 ? configureActions.ToArray() : Array.Empty<IConfigureGrainContext>();
                    configuredActivator = new ActivatorEntry(unconfiguredActivator, applicableConfigureActions);
                    _activators = _activators.SetItem(grainType, configuredActivator);
                }

                return configuredActivator;
            }
        }

        private readonly struct ActivatorEntry
        {
            public ActivatorEntry(
                IGrainContextActivator activator,
                IConfigureGrainContext[] configureActions)
            {
                this.Activator = activator;
                this.ConfigureActions = configureActions;
            }

            public IGrainContextActivator Activator { get; }

            public IConfigureGrainContext[] ConfigureActions { get; }
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
        bool TryGet(GrainType grainType, out IGrainContextActivator activator);
    }
   
    /// <summary>
    /// Creates a grain context for the given grain address.
    /// </summary>
    public interface IGrainContextActivator
    {
        /// <summary>
        /// Creates a grain context for the given grain address.
        /// </summary>
        public IGrainContext CreateContext(ActivationAddress address);
    }

    /// <summary>
    /// Provides a <see cref="IConfigureGrainContext"/> instance for the provided grain type.
    /// </summary>
    public interface IConfigureGrainContextProvider
    {
        /// <summary>
        /// Provides a <see cref="IConfigureGrainContext"/> instance for the provided grain type.
        /// </summary>
        bool TryGetConfigurator(GrainType grainType, GrainProperties properties, out IConfigureGrainContext configurator);
    }

    /// <summary>
    /// Configures the provided grain context.
    /// </summary>
    public interface IConfigureGrainContext
    {
        /// <summary>
        /// Configures the provided grain context.
        /// </summary>
        void Configure(IGrainContext context);
    }

    /// <summary>
    /// Resolves components which are common to all instances of a given grain type.
    /// </summary>
    public class GrainTypeComponentsResolver
    {
        private readonly ConcurrentDictionary<GrainType, GrainTypeComponents> _components = new ConcurrentDictionary<GrainType, GrainTypeComponents>();
        private readonly IConfigureGrainTypeComponents[] _configurators;
        private readonly GrainPropertiesResolver _resolver;
        private readonly Func<GrainType, GrainTypeComponents> _createFunc;

        public GrainTypeComponentsResolver(IEnumerable<IConfigureGrainTypeComponents> configurators, GrainPropertiesResolver resolver)
        {
            _configurators = configurators.ToArray();
            _resolver = resolver;
            _createFunc = this.Create;
        }

        /// <summary>
        /// Returns shared grain components for the provided grain type.
        /// </summary>
        public GrainTypeComponents GetComponents(GrainType grainType) => _components.GetOrAdd(grainType, _createFunc);

        private GrainTypeComponents Create(GrainType grainType)
        {
            var result = new GrainTypeComponents();
            var properties = _resolver.GetGrainProperties(grainType);
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
        void Configure(GrainType grainType, GrainProperties properties, GrainTypeComponents shared);
    }

    /// <summary>
    /// Components common to all grains of a given <see cref="GrainType"/>.
    /// </summary>
    public class GrainTypeComponents
    {
        private Dictionary<Type, object> _components = new Dictionary<Type, object>();

        /// <summary>
        /// Gets a component.
        /// </summary>
        /// <typeparam name="TComponent">The type specified in the corresponding <see cref="SetComponent{TComponent}"/> call.</typeparam>
        public TComponent GetComponent<TComponent>()
        {
            if (_components is null) return default;
            _components.TryGetValue(typeof(TComponent), out var resultObj);
            return (TComponent)resultObj;
        }

        /// <summary>
        /// Registers a component.
        /// </summary>
        /// <typeparam name="TComponent">The type which can be used as a key to <see cref="GetComponent{TComponent}"/>.</typeparam>
        public void SetComponent<TComponent>(TComponent instance)
        {
            if (instance == null)
            {
                _components?.Remove(typeof(TComponent));
                return;
            }

            if (_components is null) _components = new Dictionary<Type, object>();
            _components[typeof(TComponent)] = instance;
        }
    }

    internal class ReentrantSharedComponentsConfigurator : IConfigureGrainTypeComponents
    {
        public void Configure(GrainType grainType, GrainProperties properties, GrainTypeComponents shared)
        {
            if (properties.Properties.TryGetValue(WellKnownGrainTypeProperties.Reentrant, out var value) && bool.Parse(value))
            {
                var component = shared.GetComponent<GrainCanInterleave>();
                if (component is null)
                {
                    component = new GrainCanInterleave();
                    shared.SetComponent<GrainCanInterleave>(component);
                }

                component.MayInterleavePredicates.Add(_ => true);
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
            if (properties.Properties.TryGetValue(WellKnownGrainTypeProperties.MayInterleavePredicate, out var value)
                && _grainClassMap.TryGetGrainClass(grainType, out var grainClass))
            {
                var predicate = GetMayInterleavePredicate(grainClass);
                configurator = new MayInterleaveConfigurator(message => predicate((InvokeMethodRequest)message.BodyObject));
                return true;
            }

            configurator = null;
            return false;
        }

        /// <summary>
        /// Returns interleave predicate depending on whether class is marked with <see cref="MayInterleaveAttribute"/> or not.
        /// </summary>
        /// <param name="grainType">Grain class.</param>
        private static Func<InvokeMethodRequest, bool> GetMayInterleavePredicate(Type grainType)
        {
            var attribute = grainType.GetCustomAttribute<MayInterleaveAttribute>();
            if (attribute is null)
            {
                return null;
            }

            var callbackMethodName = attribute.CallbackMethodName;
            var method = grainType.GetMethod(callbackMethodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (method == null)
            {
                throw new InvalidOperationException(
                    $"Class {grainType.FullName} doesn't declare public static method " +
                    $"with name {callbackMethodName} specified in MayInterleave attribute");
            }

            if (method.ReturnType != typeof(bool) ||
                method.GetParameters().Length != 1 ||
                method.GetParameters()[0].ParameterType != typeof(InvokeMethodRequest))
            {
                throw new InvalidOperationException(
                    $"Wrong signature of callback method {callbackMethodName} " +
                    $"specified in MayInterleave attribute for grain class {grainType.FullName}. \n" +
                    $"Expected: public static bool {callbackMethodName}(InvokeMethodRequest req)");
            }

            var parameter = Expression.Parameter(typeof(InvokeMethodRequest));
            var call = Expression.Call(null, method, parameter);
            var predicate = Expression.Lambda<Func<InvokeMethodRequest, bool>>(call, parameter).Compile();

            return predicate;
        }
    }

    internal class MayInterleaveConfigurator : IConfigureGrainContext
    {
        private readonly Func<Message, bool> _mayInterleavePredicate;

        public MayInterleaveConfigurator(Func<Message, bool> mayInterleavePredicate)
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
        public List<Func<Message, bool>> MayInterleavePredicates { get; } = new List<Func<Message, bool>>();
        public bool MayInterleave(Message message)
        {
            foreach (var predicate in this.MayInterleavePredicates)
            {
                if (predicate(message)) return true;
            }

            return false;
        }
    }

    internal class ConfigureDefaultGrainActivator : IConfigureGrainTypeComponents
    {
        private readonly GrainClassMap _grainClassMap;
        private readonly ConstructorArgumentFactory _constructorArgumentFactory;

        public ConfigureDefaultGrainActivator(GrainClassMap grainClassMap, IServiceProvider serviceProvider)
        {
            _constructorArgumentFactory = new ConstructorArgumentFactory(serviceProvider);
            _grainClassMap = grainClassMap;
        }

        public void Configure(GrainType grainType, GrainProperties properties, GrainTypeComponents shared)
        {
            if (shared.GetComponent<IGrainActivator>() is object) return;

            if (!_grainClassMap.TryGetGrainClass(grainType, out var grainClass))
            {
                return;
            }

            var argumentFactory = _constructorArgumentFactory.CreateFactory(grainClass);
            var createGrainInstance = ActivatorUtilities.CreateFactory(grainClass, argumentFactory.ArgumentTypes);
            var instanceActivator = new DefaultGrainActivator(createGrainInstance, argumentFactory);
            shared.SetComponent<IGrainActivator>(instanceActivator);
        }

        internal class DefaultGrainActivator : IGrainActivator
        {
            private readonly ObjectFactory _factory;
            private readonly ConstructorArgumentFactory.ArgumentFactory _argumentFactory;

            public DefaultGrainActivator(ObjectFactory factory, ConstructorArgumentFactory.ArgumentFactory argumentFactory)
            {
                _factory = factory;
                _argumentFactory = argumentFactory;
            }

            public object CreateInstance(IGrainContext context)
            {
                var args = _argumentFactory.CreateArguments(context);
                return _factory(context.ActivationServices, args);
            }

            public async ValueTask DisposeInstance(IGrainContext context, object instance)
            {
                switch (instance)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
        }
    }
}