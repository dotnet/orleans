using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Versions;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
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
        private Dictionary<(GrainType, GrainInterfaceType), IGrainReferenceActivator> _activators = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceActivator"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="providers">The collection of grain reference activator providers.</param>
        public GrainReferenceActivator(
            IServiceProvider serviceProvider,
            IEnumerable<IGrainReferenceActivatorProvider> providers)
        {
            _serviceProvider = serviceProvider;
            _providers = providers.ToArray();
        }

        /// <summary>
        /// Creates a grain reference pointing to the specified grain id and implementing the specified grain interface type.
        /// </summary>
        /// <param name="grainId">The grain id.</param>
        /// <param name="interfaceType">The grain interface type.</param>
        /// <returns>A new grain reference.</returns>
        public GrainReference CreateReference(GrainId grainId, GrainInterfaceType interfaceType)
        {
            if (!_activators.TryGetValue((grainId.Type, interfaceType), out var entry))
            {
                entry = CreateActivator(grainId.Type, interfaceType);
            }

            var result = entry.CreateReference(grainId);
            return result;
        }

        /// <summary>
        /// Creates a grain reference activator for the provided arguments.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="interfaceType">the grain interface type.</param>
        /// <returns>An activator for the provided arguments.</returns>
        /// <exception cref="InvalidOperationException">No suitable activator was found.</exception>
        private IGrainReferenceActivator CreateActivator(GrainType grainType, GrainInterfaceType interfaceType)
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

                    entry = activator;
                    _activators = new(_activators) { [(grainType, interfaceType)] = entry };
                }

                return entry;
            }
        }
    }

    /// <summary>
    /// Creates grain references which do not have any specified grain interface, only a target grain id.
    /// </summary>
    internal class UntypedGrainReferenceActivatorProvider : IGrainReferenceActivatorProvider
    {
        private readonly CopyContextPool _copyContextPool;
        private readonly CodecProvider _codecProvider;
        private readonly GrainVersionManifest _versionManifest;
        private readonly IServiceProvider _serviceProvider;
        private IGrainReferenceRuntime _grainReferenceRuntime;

        /// <summary>
        /// Initializes a new instance of the <see cref="UntypedGrainReferenceActivatorProvider"/> class.
        /// </summary>
        /// <param name="manifest">The grain version manifest.</param>
        /// <param name="copyContextPool">The copy context pool.</param>
        /// <param name="codecProvider">The serialization codec provider.</param>
        /// <param name="serviceProvider">The service provider.</param>
        public UntypedGrainReferenceActivatorProvider(
            GrainVersionManifest manifest,
            CodecProvider codecProvider,
            CopyContextPool copyContextPool,
            IServiceProvider serviceProvider)
        {
            _versionManifest = manifest;
            _codecProvider = codecProvider;
            _copyContextPool = copyContextPool;
            _serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public bool TryGet(GrainType grainType, GrainInterfaceType interfaceType, out IGrainReferenceActivator activator)
        {
            if (!interfaceType.IsDefault)
            {
                activator = default;
                return false;
            }

            var interfaceVersion = _versionManifest.GetLocalVersion(interfaceType);

            var runtime = _grainReferenceRuntime ??= _serviceProvider.GetRequiredService<IGrainReferenceRuntime>();
            var shared = new GrainReferenceShared(
                grainType,
                interfaceType,
                interfaceVersion,
                runtime,
                InvokeMethodOptions.None,
                _codecProvider,
                _copyContextPool,
                _serviceProvider);
            activator = new UntypedGrainReferenceActivator(shared);
            return true;
        }

        /// <summary>
        /// Activator for grain references which have no specified grain interface, only a target grain id.
        /// </summary>
        private class UntypedGrainReferenceActivator : IGrainReferenceActivator
        {
            private readonly GrainReferenceShared _shared;

            /// <summary>
            /// Initializes a new instance of the <see cref="UntypedGrainReferenceActivator "/> class.
            /// </summary>
            /// <param name="shared">The shared functionality for all grains of a given type.</param>
            public UntypedGrainReferenceActivator(GrainReferenceShared shared)
            {
                _shared = shared;
            }

            /// <inheritdoc />
            public GrainReference CreateReference(GrainId grainId)
            {
                return GrainReference.FromGrainId(_shared, grainId);
            }
        }
    }

    /// <summary>
    /// Provides functionality for mapping from a <see cref="GrainInterfaceType"/> to the corresponding generated proxy type.
    /// </summary>
    internal class RpcProvider
    {
        private readonly TypeConverter _typeConverter;
        private readonly Dictionary<GrainInterfaceType, Type> _mapping;

        /// <summary>
        /// Initializes a new  instance of the <see cref="RpcProvider"/> class.
        /// </summary>
        /// <param name="config">The local type manifest.</param>
        /// <param name="resolver">The grain interface type to grain type resolver.</param>
        /// <param name="typeConverter">The type converter, for generic parameter.</param>
        public RpcProvider(
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

        /// <summary>
        /// Gets the generated proxy object type corresponding to the specified <see cref="GrainInterfaceType"/>.
        /// </summary>
        /// <param name="interfaceType">The grain interface type.</param>
        /// <param name="result">The proxy object type.</param>
        /// <returns>A value indicating whether a suitable type was found and was able to be constructed.</returns>
        public bool TryGet(GrainInterfaceType interfaceType, [NotNullWhen(true)] out Type result)
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

            if (args is not null)
            {
                result = result.MakeGenericType(args);
            }

            return true;
        }
    }

    /// <summary>
    /// Creates grain references using generated proxy objects.
    /// </summary>
    internal class GrainReferenceActivatorProvider : IGrainReferenceActivatorProvider
    {
        private readonly CopyContextPool _copyContextPool;
        private readonly CodecProvider _codecProvider;
        private readonly IServiceProvider _serviceProvider;
        private readonly GrainPropertiesResolver _propertiesResolver;
        private readonly RpcProvider _rpcProvider;
        private readonly GrainVersionManifest _grainVersionManifest;
        private IGrainReferenceRuntime _grainReferenceRuntime;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceActivatorProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="propertiesResolver">The grain property resolver.</param>
        /// <param name="rpcProvider">The proxy object type provider.</param>
        /// <param name="copyContextPool">The copy context pool.</param>
        /// <param name="codecProvider">The serialization codec provider.</param>
        /// <param name="grainVersionManifest">The grain version manifest.</param>
        public GrainReferenceActivatorProvider(
            IServiceProvider serviceProvider,
            GrainPropertiesResolver propertiesResolver,
            RpcProvider rpcProvider,
            CopyContextPool copyContextPool,
            CodecProvider codecProvider,
            GrainVersionManifest grainVersionManifest)
        {
            _serviceProvider = serviceProvider;
            _propertiesResolver = propertiesResolver;
            _rpcProvider = rpcProvider;
            _copyContextPool = copyContextPool;
            _codecProvider = codecProvider;
            _grainVersionManifest = grainVersionManifest;
        }

        /// <inheritdoc />
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
            var shared = new GrainReferenceShared(
                grainType,
                interfaceType,
                interfaceVersion,
                runtime,
                invokeMethodOptions,
                _codecProvider,
                _copyContextPool,
                _serviceProvider);
            activator = new GrainReferenceActivator(proxyType, shared);
            return true;
        }

        /// <summary>
        /// Creates grain references for a given grain type and grain interface type.
        /// </summary>
        private sealed class GrainReferenceActivator : IGrainReferenceActivator
        {
            private readonly GrainReferenceShared _shared;
            private readonly Func<GrainReferenceShared, IdSpan, GrainReference> _create;

            /// <summary>
            /// Initializes a new instance of the <see cref="GrainReferenceActivator"/> class.
            /// </summary>
            /// <param name="referenceType">The generated proxy object type.</param>
            /// <param name="shared">The functionality shared between all grain references for a specified grain type and grain interface type.</param>
            public GrainReferenceActivator(Type referenceType, GrainReferenceShared shared)
            {
                _shared = shared;

                var ctor = referenceType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, new[] { typeof(GrainReferenceShared), typeof(IdSpan) })
                    ?? throw new SerializerException("Invalid proxy type: " + referenceType);

                var method = new DynamicMethod(referenceType.Name, typeof(GrainReference), new[] { typeof(object), typeof(GrainReferenceShared), typeof(IdSpan) });
                var il = method.GetILGenerator();
                // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Ret);
                _create = method.CreateDelegate<Func<GrainReferenceShared, IdSpan, GrainReference>>();
            }

            public GrainReference CreateReference(GrainId grainId) => _create(_shared, grainId.Key);
        }
    }

    /// <summary>
    /// Functionality for getting the appropriate <see cref="IGrainReferenceActivator"/> for a given <see cref="GrainType"/> and <see cref="GrainInterfaceType"/>.
    /// </summary>
    public interface IGrainReferenceActivatorProvider
    {
        /// <summary>
        /// Gets a grain reference activator for the provided arguments.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="interfaceType">The grain interface type.</param>
        /// <param name="activator">The grain activator.</param>
        /// <returns>A value indicating whether a suitable grain activator was found.</returns>
        bool TryGet(GrainType grainType, GrainInterfaceType interfaceType, [NotNullWhen(true)] out IGrainReferenceActivator activator);
    }

    /// <summary>
    /// Creates grain references.
    /// </summary>
    public interface IGrainReferenceActivator
    {
        /// <summary>
        /// Creates a new grain reference.
        /// </summary>
        /// <param name="grainId">The grain id.</param>
        /// <returns>A new grain reference.</returns>
        public GrainReference CreateReference(GrainId grainId);
    }
}
