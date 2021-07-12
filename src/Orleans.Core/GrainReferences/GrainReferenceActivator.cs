
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Versions;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;

namespace Orleans.GrainReferences
{
    /// <summary>
    /// The central point for creating <see cref="GrainReference"/> instances.
    /// </summary>
    public sealed class GrainReferenceActivator
    {
        private readonly object _lockObj = new object();
        private readonly IServiceProvider _serviceProvider;
        private readonly IGrainReferenceActivatorProvider[] _providers;
        private Dictionary<(GrainType, GrainInterfaceType), Entry> _activators = new Dictionary<(GrainType, GrainInterfaceType), Entry>();

        public GrainReferenceActivator(
            IServiceProvider serviceProvider,
            IEnumerable<IGrainReferenceActivatorProvider> providers)
        {
            _serviceProvider = serviceProvider;
            _providers = providers.ToArray();
        }

        public GrainReference CreateReference(GrainId grainId, GrainInterfaceType interfaceType)
        {
            if (!_activators.TryGetValue((grainId.Type, interfaceType), out var entry))
            {
                entry = CreateActivator(grainId.Type, interfaceType);
            }

            var result = entry.Activator.CreateReference(grainId);
            return result;
        }

        private Entry CreateActivator(GrainType grainType, GrainInterfaceType interfaceType)
        {
            lock (_lockObj)
            {
                if (!_activators.TryGetValue((grainType, interfaceType), out var entry))
                {
                    IGrainReferenceActivator activator = null;
                    foreach (var provider in _providers)
                    {
                        if (provider.TryGet(grainType, interfaceType, out activator))
                        {
                            break;
                        }
                    }

                    if (activator is null)
                    {
                        throw new InvalidOperationException($"Unable to find an {nameof(IGrainReferenceActivatorProvider)} for grain type {grainType}");
                    }

                    entry = new Entry(activator);
                    _activators = new Dictionary<(GrainType, GrainInterfaceType), Entry>(_activators) { [(grainType, interfaceType)] = entry };
                }

                return entry;
            }
        }

        private readonly struct Entry
        {
            public Entry(IGrainReferenceActivator activator)
            {
                this.Activator = activator;
            }

            public IGrainReferenceActivator Activator { get; }
        }
    }

    internal class UntypedGrainReferenceActivatorProvider : IGrainReferenceActivatorProvider
    {
        private readonly GrainVersionManifest _versionManifest;
        private readonly IServiceProvider _serviceProvider;
        private IGrainReferenceRuntime _grainReferenceRuntime;

        public UntypedGrainReferenceActivatorProvider(GrainVersionManifest manifest, IServiceProvider serviceProvider)
        {
            _versionManifest = manifest;
            _serviceProvider = serviceProvider;
        }

        public bool TryGet(GrainType grainType, GrainInterfaceType interfaceType, out IGrainReferenceActivator activator)
        {
            if (!interfaceType.IsDefault)
            {
                activator = default;
                return false;
            }

            var interfaceVersion = _versionManifest.GetLocalVersion(interfaceType);
       
            var runtime = _grainReferenceRuntime ??= _serviceProvider.GetRequiredService<IGrainReferenceRuntime>();
            var shared = new GrainReferenceShared(grainType, interfaceType, interfaceVersion, runtime, InvokeMethodOptions.None, _serviceProvider);
            activator = new UntypedGrainReferenceActivator(shared);
            return true;
        }

        private class UntypedGrainReferenceActivator : IGrainReferenceActivator
        {
            private readonly GrainReferenceShared _shared;

            public UntypedGrainReferenceActivator(GrainReferenceShared shared)
            {
                _shared = shared;
            }

            public GrainReference CreateReference(GrainId grainId)
            {
                return GrainReference.FromGrainId(_shared, grainId);
            }
        }
    }

    internal class NewRpcProvider
    {
        private readonly TypeConverter _typeConverter;
        private readonly Dictionary<GrainInterfaceType, Type> _mapping;

        public NewRpcProvider(
            IOptions<TypeManifestOptions> config,
            GrainInterfaceTypeResolver resolver,
            TypeConverter typeConverter)
        {
            _typeConverter = typeConverter;
            var proxyTypes = config.Value.InterfaceProxies;
            _mapping = new Dictionary<GrainInterfaceType, Type>();
            foreach (var proxyType in proxyTypes)
            {
                if (!typeof(IAddressable).IsAssignableFrom(proxyType))
                {
                    continue;
                }

                var type = proxyType switch
                {
                    { IsGenericType: true } => proxyType.GetGenericTypeDefinition(),
                    _ => proxyType
                };

                var grainInterface = GetMainInterface(type);
                var id = resolver.GetGrainInterfaceType(grainInterface);
                _mapping[id] = type;
            }

            static Type GetMainInterface(Type t)
            {
                var all = t.GetInterfaces();
                Type result = null;
                foreach (var candidate in all)
                {
                    if (result is null)
                    {
                        result = candidate;
                    }
                    else
                    {
                        if (result.IsAssignableFrom(candidate))
                        {
                            result = candidate;
                        }
                    }
                }

                return result switch
                {
                    { IsGenericType: true } => result.GetGenericTypeDefinition(),
                    _ => result
                };
            }
        }

        public bool TryGet(GrainInterfaceType interfaceType, out Type result)
        {
            GrainInterfaceType lookupId;
            Type[] args;
            if (GenericGrainInterfaceType.TryParse(interfaceType, out var genericId))
            {
                lookupId = genericId.GetGenericGrainType().Value;
                args = genericId.GetArguments(_typeConverter);
            }
            else
            {
                lookupId = interfaceType;
                args = default;
            }

            if (!_mapping.TryGetValue(lookupId, out result))
            {
                return false;
            }

            if (args is Type[])
            {
                result = result.MakeGenericType(args);
            }

            return true;
        }
    }

    internal class GrainReferenceActivatorProvider : IGrainReferenceActivatorProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly GrainPropertiesResolver _propertiesResolver;
        private readonly NewRpcProvider _rpcProvider;
        private readonly GrainVersionManifest _grainVersionManifest;
        private IGrainReferenceRuntime _grainReferenceRuntime;

        public GrainReferenceActivatorProvider(
            IServiceProvider serviceProvider,
            GrainPropertiesResolver propertiesResolver,
            NewRpcProvider rpcProvider,
            GrainVersionManifest grainVersionManifest)
        {
            _serviceProvider = serviceProvider;
            _propertiesResolver = propertiesResolver;
            _rpcProvider = rpcProvider;
            _grainVersionManifest = grainVersionManifest;
        }

        public bool TryGet(GrainType grainType, GrainInterfaceType interfaceType, out IGrainReferenceActivator activator)
        {
            if (!_rpcProvider.TryGet(interfaceType, out var proxyType))
            {
                activator = default;
                return false;
            }

            var unordered = false;
            var properties = _propertiesResolver.GetGrainProperties(grainType);
            if (properties.Properties.TryGetValue(WellKnownGrainTypeProperties.Unordered, out var unorderedString)
                && string.Equals("true", unorderedString, StringComparison.OrdinalIgnoreCase))
            {
                unordered = true;
            }

            var interfaceVersion = _grainVersionManifest.GetLocalVersion(interfaceType);

            var invokeMethodOptions = unordered ? InvokeMethodOptions.Unordered : InvokeMethodOptions.None;
            var runtime = _grainReferenceRuntime ??= _serviceProvider.GetRequiredService<IGrainReferenceRuntime>();
            var shared = new GrainReferenceShared(grainType, interfaceType, interfaceVersion, runtime, invokeMethodOptions, _serviceProvider);
            activator = new GrainReferenceActivator(proxyType, shared);
            return true;
        }

        private class GrainReferenceActivator : IGrainReferenceActivator
        {
            private readonly Type _referenceType;
            private readonly GrainReferenceShared _shared;

            public GrainReferenceActivator(Type referenceType, GrainReferenceShared shared)
            {
                _referenceType = referenceType;
                _shared = shared;
            }

            public GrainReference CreateReference(GrainId grainId)
            {
                return (GrainReference)Activator.CreateInstance(
                    type: _referenceType,
                    bindingAttr: BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: new object[] { _shared, grainId.Key },
                    culture: CultureInfo.InvariantCulture);
            }
        }
    }

    public interface IGrainReferenceActivatorProvider
    {
        bool TryGet(GrainType grainType, GrainInterfaceType interfaceType, out IGrainReferenceActivator activator);
    }

    public interface IGrainReferenceActivator
    {
        public GrainReference CreateReference(GrainId grainId);
    }
}
