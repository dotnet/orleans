
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.ApplicationParts;
using Orleans.CodeGeneration;
using Orleans.Metadata;
using Orleans.Runtime;

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
        private Dictionary<(GrainType, GrainInterfaceId), Entry> _activators = new Dictionary<(GrainType, GrainInterfaceId), Entry>();

        public GrainReferenceActivator(
            IServiceProvider serviceProvider,
            IEnumerable<IGrainReferenceActivatorProvider> providers)
        {
            _serviceProvider = serviceProvider;
            _providers = providers.ToArray();
        }

        public GrainReference CreateReference(GrainId grainId, GrainInterfaceId interfaceId)
        {
            if (!_activators.TryGetValue((grainId.Type, interfaceId), out var entry))
            {
                entry = CreateActivator(grainId.Type, interfaceId);
            }

            var result = entry.Activator.CreateReference(grainId);
            return result;
        }

        private Entry CreateActivator(GrainType grainType, GrainInterfaceId interfaceId)
        {
            lock (_lockObj)
            {
                if (!_activators.TryGetValue((grainType, interfaceId), out var entry))
                {
                    IGrainReferenceActivator activator = null;
                    foreach (var provider in _providers)
                    {
                        if (provider.TryGet(grainType, interfaceId, out activator))
                        {
                            break;
                        }
                    }

                    if (activator is null)
                    {
                        throw new InvalidOperationException($"Unable to find an {nameof(IGrainReferenceActivatorProvider)} for grain type {grainType}");
                    }

                    entry = new Entry(activator);
                    _activators = new Dictionary<(GrainType, GrainInterfaceId), Entry>(_activators) { [(grainType, interfaceId)] = entry };
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
        private readonly IServiceProvider _serviceProvider;
        private IGrainReferenceRuntime _grainReferenceRuntime;

        public UntypedGrainReferenceActivatorProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public bool TryGet(GrainType grainType, GrainInterfaceId interfaceId, out IGrainReferenceActivator activator)
        {
            if (!interfaceId.IsDefault)
            {
                activator = default;
                return false;
            }
       
            var runtime = _grainReferenceRuntime ??= _serviceProvider.GetRequiredService<IGrainReferenceRuntime>();
            var shared = new GrainReferenceShared(grainType, interfaceId, runtime, InvokeMethodOptions.None);
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

    internal class ImrRpcProvider
    {
        private readonly TypeConverter _typeConverter;
        private readonly Dictionary<GrainInterfaceId, (Type ReferenceType, Type InvokerType)> _mapping;
        public ImrRpcProvider(
        IServiceProvider serviceProvider,
        IApplicationPartManager appParts,
        GrainInterfaceIdResolver resolver,
        TypeConverter typeConverter)
        {
            _typeConverter = typeConverter;
            var interfaces = appParts.CreateAndPopulateFeature<GrainInterfaceFeature>();
            _mapping = new Dictionary<GrainInterfaceId, (Type ReferenceType, Type InvokerType)>();
            foreach (var @interface in interfaces.Interfaces)
            {
                var id = resolver.GetGrainInterfaceId(@interface.InterfaceType);
                _mapping[id] = (@interface.ReferenceType, @interface.InvokerType);
            }
        }

        public bool TryGet(GrainInterfaceId interfaceId, out (Type ReferenceType, Type InvokerType) result)
        {
            GrainInterfaceId lookupId;
            Type[] args;
            if (GenericGrainInterfaceId.TryParse(interfaceId, out var genericId))
            {
                lookupId = genericId.GetGenericGrainType().Value;
                args = genericId.GetArguments(_typeConverter);
            }
            else
            {
                lookupId = interfaceId;
                args = default;
            }

            if (!_mapping.TryGetValue(lookupId, out var mapping))
            {
                result = default;
                return false;
            }

            var (referenceType, invokerType) = mapping;

            if (args is Type[])
            {
                referenceType = referenceType.MakeGenericType(args);
                invokerType = invokerType.MakeGenericType(args);
            }

            result = (referenceType, invokerType);
            return true;
        }
    }

    internal class ImrGrainMethodInvokerProvider
    {
        private readonly ImrRpcProvider _rpcProvider;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<GrainInterfaceId, IGrainMethodInvoker> _invokers = new ConcurrentDictionary<GrainInterfaceId, IGrainMethodInvoker>();

        public ImrGrainMethodInvokerProvider(ImrRpcProvider rpcProvider, IServiceProvider serviceProvider)
        {
            _rpcProvider = rpcProvider;
            _serviceProvider = serviceProvider;
        }

        public bool TryGet(GrainInterfaceId interfaceId, out IGrainMethodInvoker invoker)
        {
            if (_invokers.TryGetValue(interfaceId, out invoker))
            {
                return true;
            }

            if (!_rpcProvider.TryGet(interfaceId, out var types))
            {
                invoker = default;
                return false;
            }

            _invokers[interfaceId] = invoker = (IGrainMethodInvoker)ActivatorUtilities.CreateInstance(_serviceProvider, types.InvokerType);
            return true;
        }
    }

    internal class ImrGrainReferenceActivatorProvider : IGrainReferenceActivatorProvider
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly GrainPropertiesResolver _propertiesResolver;
        private readonly ImrRpcProvider _rpcProvider;
        private IGrainReferenceRuntime _grainReferenceRuntime;

        public ImrGrainReferenceActivatorProvider(
            IServiceProvider serviceProvider,
            GrainPropertiesResolver propertiesResolver,
            ImrRpcProvider rpcProvider)
        {
            _serviceProvider = serviceProvider;
            _propertiesResolver = propertiesResolver;
            _rpcProvider = rpcProvider;
        }

        public bool TryGet(GrainType grainType, GrainInterfaceId interfaceId, out IGrainReferenceActivator activator)
        {
            if (!_rpcProvider.TryGet(interfaceId, out var types))
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

            var invokeMethodOptions = unordered ? InvokeMethodOptions.Unordered : InvokeMethodOptions.None;
            var runtime = _grainReferenceRuntime ??= _serviceProvider.GetRequiredService<IGrainReferenceRuntime>();
            var shared = new GrainReferenceShared(grainType, interfaceId, runtime, invokeMethodOptions);
            activator = new ImrGrainReferenceActivator(types.ReferenceType, shared);
            return true;
        }

        private class ImrGrainReferenceActivator : IGrainReferenceActivator
        {
            private readonly Type _referenceType;
            private readonly GrainReferenceShared _shared;

            public ImrGrainReferenceActivator(Type referenceType, GrainReferenceShared shared)
            {
                _referenceType = referenceType;
                _shared = shared;
            }

            public GrainReference CreateReference(GrainId grainId)
            {
                return (GrainReference)Activator.CreateInstance(
                    type: _referenceType,
                    bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    args: new object[] { _shared, grainId.Key },
                    culture: CultureInfo.InvariantCulture);
            }
        }
    }

    public interface IGrainReferenceActivatorProvider
    {
        bool TryGet(GrainType grainType, GrainInterfaceId interfaceId, out IGrainReferenceActivator activator);
    }

    public interface IGrainReferenceActivator
    {
        public GrainReference CreateReference(GrainId grainId);
    }
}
