using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Provides access to serializers and related objects.
    /// </summary>
    public sealed class CodecProvider : ICodecProvider
    {
        private static readonly Type ObjectType = typeof(object);

#if NET9_0_OR_GREATER
        private readonly Lock _initializationLock = new();
#else
        private readonly object _initializationLock = new();
#endif

        private readonly ConcurrentDictionary<Type, IFieldCodec> _untypedCodecs = new();
        private readonly ConcurrentDictionary<Type, IFieldCodec> _typedCodecs = new();
        private readonly ConcurrentDictionary<Type, IBaseCodec> _typedBaseCodecs = new();
        private readonly ConcurrentDictionary<Type, IDeepCopier> _untypedCopiers = new();
        private readonly ConcurrentDictionary<Type, IDeepCopier> _typedCopiers = new();

        private readonly ConcurrentDictionary<Type, object> _serializerServices = new();
        // A null target groups array and bare-parameter patterns for shape matching.
        private readonly Dictionary<(Type Contract, Type? Target), List<(Type Implementation, SerializationContract Contract)>> _implementationCandidates = new();
        private readonly List<IGeneralizedCodec> _generalizedCodecs = new();
        private readonly List<ISpecializableCodec> _specializableCodecs = new();
        private readonly List<IGeneralizedBaseCodec> _generalizedBaseCodecs = new();
        private readonly List<ISpecializableBaseCodec> _specializableBaseCodecs = new();
        private readonly List<IGeneralizedCopier> _generalizedCopiers = new();
        private readonly List<ISpecializableCopier> _specializableCopiers = new();
        private readonly ObjectCodec _objectCodec = new();
        private readonly VoidCodec _voidCodec = new();
        private readonly ObjectCopier _objectCopier = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly VoidCopier _voidCopier = new();
        private readonly TypeManifestOptions _manifest;
        private readonly IServiceProvider _constructionServices;
        private bool _initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="CodecProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="codecConfiguration">The codec configuration.</param>
        public CodecProvider(IServiceProvider serviceProvider, IOptions<TypeManifestOptions> codecConfiguration)
        {
            _serviceProvider = serviceProvider;
            _constructionServices = new ConstructionServiceProvider(this);
            _manifest = codecConfiguration.Value;
            ConsumeMetadata(codecConfiguration);
        }

        /// <inheritdoc/>
        public IServiceProvider Services => _manifest.SerializerServiceFactories.Count == 0 ? _serviceProvider : _constructionServices;

        private void Initialize()
        {
            lock (_initializationLock)
            {
                if (_initialized)
                {
                    return;
                }

                _generalizedCodecs.AddRange(_serviceProvider.GetServices<IGeneralizedCodec>());
                _generalizedBaseCodecs.AddRange(_serviceProvider.GetServices<IGeneralizedBaseCodec>());
                _generalizedCopiers.AddRange(_serviceProvider.GetServices<IGeneralizedCopier>());

                _specializableCodecs.AddRange(_serviceProvider.GetServices<ISpecializableCodec>());
                _specializableCopiers.AddRange(_serviceProvider.GetServices<ISpecializableCopier>());
                _specializableBaseCodecs.AddRange(_serviceProvider.GetServices<ISpecializableBaseCodec>());

                _initialized = true;
            }
        }

        private void ConsumeMetadata(IOptions<TypeManifestOptions> codecConfiguration)
        {
            var metadata = codecConfiguration.Value;
            var candidates = new List<(Type? Target, Type Implementation, SerializationContract Contract, int LegacyOrder)>();
            AddFromMetadata(metadata.SerializerTypes, metadata.SerializerContracts, typeof(IBaseCodec<>));
            AddFromMetadata(metadata.SerializerTypes, metadata.SerializerContracts, typeof(IValueSerializer<>));
            AddFromMetadata(metadata.SerializerTypes, metadata.SerializerContracts, typeof(IFieldCodec<>));
            AddFromMetadata(metadata.FieldCodecTypes, metadata.SerializerContracts, typeof(IFieldCodec<>));
            AddFromMetadata(metadata.ActivatorTypes, metadata.ActivatorContracts, typeof(IActivator<>));
            AddFromMetadata(metadata.CopierTypes, metadata.CopierContracts, typeof(IDeepCopier<>));
            AddFromMetadata(metadata.ConverterTypes, metadata.ConverterContracts, typeof(IConverter<,>));
            AddFromMetadata(metadata.CopierTypes, metadata.CopierContracts, typeof(IBaseCopier<>));

            foreach (var candidate in candidates
                .OrderBy(static candidate => candidate.Contract.RegistrationOrder ?? candidate.LegacyOrder)
                .ThenBy(static candidate => candidate.Contract.RegistrationOrder.HasValue))
            {
                var key = (candidate.Contract.ContractType, candidate.Target);
                if (!_implementationCandidates.TryGetValue(key, out var registrations))
                {
                    _implementationCandidates[key] = registrations = new();
                }

                registrations.Add((candidate.Implementation, candidate.Contract));
            }

#if NET5_0_OR_GREATER
            [UnconditionalSuppressMessage(
                "Trimming",
                "IL2065",
                Justification = "Legacy implementation-only registrations preserve implemented interfaces through annotated TypeManifestOptions methods. Explicit contract registrations are consumed directly. The manifest collections and materialized Type arrays cannot retain the legacy annotations.")]
#endif
            void AddFromMetadata(
                HashSet<Type> metadataCollection,
                Dictionary<Type, List<SerializationContract>> contracts,
                Type genericType)
            {
                Debug.Assert(genericType.GetGenericArguments().Length >= 1);

                var types = metadataCollection.ToArray();
                var legacyOrders = new int[types.Length];
                var nextOrder = int.MaxValue;
                // Anchor legacy entries before subsequent collection members' explicit registrations.
                for (var i = types.Length - 1; i >= 0; i--)
                {
                    legacyOrders[i] = nextOrder;
                    if (contracts.TryGetValue(types[i], out var registrations))
                    {
                        foreach (var registration in registrations)
                        {
                            nextOrder = Math.Min(nextOrder, registration.RegistrationOrder!.Value);
                        }
                    }
                }

                for (var i = 0; i < types.Length; i++)
                {
                    var type = types[i];
                    // Open built-in array implementations are intrinsic fallbacks, after registered converters.
                    if (type == typeof(ArrayCodec<>) || type == typeof(ArrayCopier<>))
                    {
                        continue;
                    }

                    if (contracts.TryGetValue(type, out var registrations))
                    {
                        foreach (var registration in registrations)
                        {
                            if (registration.ContractType != genericType)
                            {
                                continue;
                            }

                            var target = registration.TargetDescription is { } description
                                ? description.Type is null
                                    ? null
                                    : description.Type is { IsGenericTypeDefinition: true } definition
                                        ? definition
                                        : ResolveSerializationType(description, type.GetGenericArguments())
                                : registration.TargetType!;
                            if (target != typeof(object))
                            {
                                candidates.Add((target, type, registration, legacyOrders[i]));
                            }
                        }

                        if (!metadata.DiscoverInterfaces(type, genericType))
                        {
                            continue;
                        }
                    }

                    var interfaces = type.GetInterfaces();
                    foreach (var @interface in interfaces)
                    {
                        if (!@interface.IsGenericType)
                        {
                            continue;
                        }

                        if (genericType != @interface.GetGenericTypeDefinition())
                        {
                            continue;
                        }

                        var genericArgument = @interface.GetGenericArguments()[0];
                        if (typeof(object) == genericArgument)
                        {
                            continue;
                        }

                        if (registrations is not null && registrations.Exists(registration =>
                            registration.ContractType == genericType
                            && (registration.TargetDescription is { } description
                                ? BindTypeArguments(description, genericArgument, new Type?[type.GetGenericArguments().Length])
                                : registration.TargetType == genericArgument
                                    || genericArgument.IsConstructedGenericType && registration.TargetType == genericArgument.GetGenericTypeDefinition())))
                        {
                            continue;
                        }

                        var legacyDescription = genericArgument.ContainsGenericParameters
                            ? SerializationType.FromType(genericArgument, type.GetGenericArguments())
                            : null;
                        var target = legacyDescription is { } shape
                            ? shape.Type
                            : genericArgument;
                        candidates.Add((target, type,
                            new SerializationContract(genericType, target, null, TargetDescription: legacyDescription), legacyOrders[i]));
                    }
                }
            }
        }

        /// <inheritdoc/>
        public IFieldCodec<TField>? TryGetCodec<TField>()
        {
            var fieldType = typeof(TField);
            if (_manifest.CodecFactories.TryGetValue(fieldType, out var factory)) return (IFieldCodec<TField>)factory(this);
            if (TryGetCached(_typedCodecs, fieldType, out var existing))
                return (IFieldCodec<TField>)existing;

            if (TryGetCodec(fieldType) is not { } untypedResult)
                return null;

            var typedResult = untypedResult switch
            {
                IFieldCodec<TField> typed => typed,
                _ when untypedResult.GetType() == typeof(AbstractTypeSerializer) => new AbstractTypeSerializerWrapper<TField>(),
                _ => new UntypedCodecWrapper<TField>(untypedResult)
            };

            return (IFieldCodec<TField>)CacheValue(_typedCodecs, fieldType, typedResult);
        }

        /// <inheritdoc/>
        public IFieldCodec GetCodec(Type fieldType)
        {
            var res = TryGetCodec(fieldType);
            if (res is null) ThrowCodecNotFound(fieldType);
            return res;
        }

        /// <inheritdoc/>
        public IFieldCodec? TryGetCodec(Type fieldType)
        {
            if (fieldType is not null && _manifest.CodecFactories.TryGetValue(fieldType, out var factory)) return factory(this);
            // If the field type is unavailable, return the void codec which can at least handle references.
            return fieldType is null ? _voidCodec
                : TryGetCached(_untypedCodecs, fieldType, out var existing) ? existing
                : TryCreateCodec(fieldType) is { } res ? CacheValue(_untypedCodecs, fieldType, res) : null;
        }

        private IFieldCodec? TryCreateCodec(Type fieldType)
        {
            try { return TryCreateCodecInner(fieldType); }
            catch (Exception exception) { RecordConstructionFailure(exception); throw; }
        }

        private IFieldCodec? TryCreateCodecInner(Type fieldType)
        {
            if (_manifest.CodecFactories.TryGetValue(fieldType, out var factory)) return factory(this);
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(fieldType);

            if (CreateCodecInstance(fieldType, fieldType.IsConstructedGenericType ? fieldType.GetGenericTypeDefinition() : fieldType) is { } res)
                return res;

            foreach (var specializableCodec in _specializableCodecs)
            {
                if (specializableCodec.IsSupportedType(fieldType))
                    return specializableCodec.GetSpecializedCodec(fieldType);
            }

            foreach (var dynamicCodec in _generalizedCodecs)
            {
                if (dynamicCodec.IsSupportedType(fieldType))
                    return dynamicCodec;
            }

            return fieldType.IsInterface || fieldType.IsAbstract ? new AbstractTypeSerializer(fieldType) : null;
        }

        /// <inheritdoc/>
        public IFieldCodec<TField> GetCodec<TField>()
        {
            var res = TryGetCodec<TField>();
            if (res is null) ThrowCodecNotFound(typeof(TField));
            return res;
        }

        /// <inheritdoc/>
        public IActivator<T> GetActivator<T>()
        {
            if (TryGetSerializerService(typeof(IActivator<T>), out var registered)) return (IActivator<T>)registered;
            var type = typeof(T);
            var searchType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

            var res = GetActivatorInner(type, searchType);
            if (res is null) ThrowActivatorNotFound(type);
            return (IActivator<T>)res;
        }

        private IBaseCodec<TField>? TryCreateBaseCodec<TField>(Type fieldType) where TField : class
        {
            try { return TryCreateBaseCodecInner<TField>(fieldType); }
            catch (Exception exception) { RecordConstructionFailure(exception); throw; }
        }

        private IBaseCodec<TField>? TryCreateBaseCodecInner<TField>(Type fieldType) where TField : class
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(fieldType);

            // Try to find the codec from the configured codecs.
            var untypedResult = CreateBaseCodecInstance(fieldType, fieldType.IsConstructedGenericType ? fieldType.GetGenericTypeDefinition() : fieldType);

            if (untypedResult is null)
            {
                foreach (var specializableCodec in _specializableBaseCodecs)
                {
                    if (specializableCodec.IsSupportedType(fieldType))
                    {
                        untypedResult = specializableCodec.GetSpecializedCodec(fieldType);
                        break;
                    }
                }

                if (untypedResult is null)
                {
                    foreach (var dynamicCodec in _generalizedBaseCodecs)
                    {
                        if (dynamicCodec.IsSupportedType(fieldType))
                        {
                            untypedResult = dynamicCodec;
                            break;
                        }
                    }

                    if (untypedResult is null)
                        return null;
                }
            }

            if (untypedResult is not IBaseCodec<TField> typedResult)
                ThrowCannotConvert(untypedResult);

            return (IBaseCodec<TField>)CacheValue(_typedBaseCodecs, fieldType, typedResult);

            static void ThrowCannotConvert(object rawCodec) => throw new InvalidOperationException($"Cannot convert codec of type {rawCodec.GetType()} to codec of type {typeof(IBaseCodec<TField>)}.");
        }

        /// <inheritdoc/>
        public IBaseCodec<TField> GetBaseCodec<TField>() where TField : class
        {
            if (TryGetSerializerService(typeof(IBaseCodec<TField>), out var registered)) return (IBaseCodec<TField>)registered;
            var type = typeof(TField);
            if (TryGetCached(_typedBaseCodecs, type, out var existing))
                return (IBaseCodec<TField>)existing;

            var result = TryCreateBaseCodec<TField>(type);
            if (result is null) ThrowBaseCodecNotFound(type);
            return result;
        }

        /// <inheritdoc/>
        public IValueSerializer<TField> GetValueSerializer<TField>() where TField : struct
        {
            if (TryGetSerializerService(typeof(IValueSerializer<TField>), out var registered)) return (IValueSerializer<TField>)registered;
            var type = typeof(TField);
            var searchType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

            var res = GetValueSerializerInner(type, searchType);
            if (res is null) ThrowValueSerializerNotFound(type);
            return (IValueSerializer<TField>)res;
        }

        /// <inheritdoc/>
        public IBaseCopier<TField> GetBaseCopier<TField>() where TField : class
        {
            if (TryGetSerializerService(typeof(IBaseCopier<TField>), out var registered)) return (IBaseCopier<TField>)registered;
            var type = typeof(TField);
            var searchType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

            var res = GetBaseCopierInner(type, searchType);
            if (res is null) ThrowBaseCopierNotFound(type);
            return (IBaseCopier<TField>)res;
        }

        /// <inheritdoc/>
        public IDeepCopier<T> GetDeepCopier<T>()
        {
            var res = TryGetDeepCopier<T>();
            if (res is null) ThrowCopierNotFound(typeof(T));
            return res;
        }

        /// <inheritdoc/>
        public IDeepCopier<T>? TryGetDeepCopier<T>()
        {
            var type = typeof(T);
            if (_manifest.CopierFactories.TryGetValue(type, out var factory)) return (IDeepCopier<T>)factory(this);
            if (TryGetCached(_typedCopiers, type, out var existing))
                return (IDeepCopier<T>)existing;

            if (TryGetDeepCopier(type) is not { } untypedResult)
                return null;

            var typedResult = untypedResult switch
            {
                IDeepCopier<T> typed => typed,
                IOptionalDeepCopier optional when optional.IsShallowCopyable() => new ShallowCopier<T>(),
                _ => new UntypedCopierWrapper<T>(untypedResult)
            };

            return (IDeepCopier<T>)CacheValue(_typedCopiers, type, typedResult);
        }

        /// <inheritdoc/>
        public IDeepCopier GetDeepCopier(Type fieldType)
        {
            var res = TryGetDeepCopier(fieldType);
            if (res is null) ThrowCopierNotFound(fieldType);
            return res;
        }

        /// <inheritdoc/>
        public IDeepCopier? TryGetDeepCopier(Type fieldType)
        {
            if (fieldType is not null && _manifest.CopierFactories.TryGetValue(fieldType, out var factory)) return factory(this);
            // If the field type is unavailable, return the void copier which can at least handle references.
            return fieldType is null ? _voidCopier
                : TryGetCached(_untypedCopiers, fieldType, out var existing) ? existing
                : TryCreateCopier(fieldType) is { } res ? CacheValue(_untypedCopiers, fieldType, res)
                : null;
        }

        private IDeepCopier? TryCreateCopier(Type fieldType)
        {
            try { return TryCreateCopierInner(fieldType); }
            catch (Exception exception) { RecordConstructionFailure(exception); throw; }
        }

        private IDeepCopier? TryCreateCopierInner(Type fieldType)
        {
            if (_manifest.CopierFactories.TryGetValue(fieldType, out var factory)) return factory(this);
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(fieldType);

            if (CreateCopierInstance(fieldType, fieldType.IsConstructedGenericType ? fieldType.GetGenericTypeDefinition() : fieldType) is { } res)
                return res;

            foreach (var specializableCopier in _specializableCopiers)
            {
                if (specializableCopier.IsSupportedType(fieldType))
                    return specializableCopier.GetSpecializedCopier(fieldType);
            }

            foreach (var dynamicCopier in _generalizedCopiers)
            {
                if (dynamicCopier.IsSupportedType(fieldType))
                    return dynamicCopier;
            }

            return fieldType.IsInterface || fieldType.IsAbstract ? _objectCopier : null;
        }

        private object? GetValueSerializerInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            object[]? constructorArguments = null;
            if (!TrySelectImplementation(typeof(IValueSerializer<>), concreteType, searchType, out var serializerType))
            {
                if (!TryGetSurrogateCodec(concreteType, searchType, out var surrogateCodecType, out constructorArguments)
                    || !typeof(IValueSerializer).IsAssignableFrom(surrogateCodecType))
                {
                    return null;
                }

                serializerType = surrogateCodecType;
            }

            if (!TryGetCached(_serializerServices, serializerType, out var result))
            {
                result = CacheValue(_serializerServices, serializerType, GetServiceOrCreateInstance(serializerType, constructorArguments));
            }

            return result;
        }

        private object? GetBaseCopierInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            object[]? constructorArguments = null;
            if (!TrySelectImplementation(typeof(IBaseCopier<>), concreteType, searchType, out var copierType))
            {
                if (!TryGetSurrogateCodec(concreteType, searchType, out var surrogateCodecType, out constructorArguments)
                    || !typeof(IBaseCopier).IsAssignableFrom(surrogateCodecType))
                {
                    return null;
                }

                copierType = surrogateCodecType;
            }

            if (!TryGetCached(_serializerServices, copierType, out var result))
            {
                result = CacheValue(_serializerServices, copierType, GetServiceOrCreateInstance(copierType, constructorArguments));
            }

            return result;
        }

        private object? GetActivatorInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            if (!TrySelectImplementation(typeof(IActivator<>), concreteType, searchType, out var activatorType))
            {
                if (searchType.IsValueType)
                {
                    activatorType = ConstructGenericImplementation(typeof(DefaultValueTypeActivator<>), concreteType);
                }
                else
                {
                    activatorType = ConstructGenericImplementation(typeof(DefaultReferenceTypeActivator<>), concreteType);
                }
            }

            if (!TryGetCached(_serializerServices, activatorType, out var result))
            {
                result = CacheValue(_serializerServices, activatorType, GetServiceOrCreateInstance(activatorType));
            }

            return result;
        }

        private static void ThrowIfUnsupportedType(Type fieldType)
        {
            if (fieldType.IsGenericTypeDefinition)
            {
                ThrowGenericTypeDefinition(fieldType);
            }

            if (fieldType.IsPointer)
            {
                ThrowPointerType(fieldType);
            }

            if (fieldType.IsByRef)
            {
                ThrowByRefType(fieldType);
            }
        }

#if NET5_0_OR_GREATER
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DefaultReferenceTypeActivator<>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DefaultValueTypeActivator<>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ConcreteTypeSerializer<,>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ValueSerializer<,>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ArrayCodec<>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(MultiDimensionalArrayCodec<,>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SurrogateCodec<,,>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ValueTypeSurrogateCodec<,,>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ArrayCopier<>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(MultiDimensionalArrayCopier<>))]
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2067",
            Justification = "Generated manifests and trim-safe manual configuration use the annotated TypeManifestOptions registration methods, which preserve public constructors. Built-in dynamically closed implementations are rooted by DynamicDependency attributes. Other implementation types are resolved from dependency injection before this activation path. The TypeManifestOptions and dictionary boundaries cannot retain the annotations.")]
#endif
        private object GetServiceOrCreateInstance(Type type, object[]? constructorArguments = null)
        {
            try
            {
                if (_manifest.SerializerServiceFactories.Count == 0) return ActivateService(type, constructorArguments);
                if (OrleansGeneratedCodeHelper.TryGetService(type, this) is { } caller) return caller;
                if (TryGetCached(_serializerServices, type, out var completed)) return completed;
                if (TryGetSerializerService(type, out var registered)) return registered;
                return ConstructService(type, () => ActivateService(type, constructorArguments), beginGraph: false);
            }
            catch (Exception exception) { RecordConstructionFailure(exception); throw; }
        }

        private bool IsRegisteredImplementation(Type type)
        {
            var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
            return _manifest.SerializerTypes.Contains(type) || _manifest.SerializerTypes.Contains(definition)
                || _manifest.FieldCodecTypes.Contains(type) || _manifest.FieldCodecTypes.Contains(definition)
                || _manifest.CopierTypes.Contains(type) || _manifest.CopierTypes.Contains(definition)
                || _manifest.ActivatorTypes.Contains(type) || _manifest.ActivatorTypes.Contains(definition)
                || _manifest.ConverterTypes.Contains(type) || _manifest.ConverterTypes.Contains(definition);
        }

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage("Trimming", "IL2067",
            Justification = "Manifest registrations preserve implementation constructors through annotated TypeManifestOptions methods before the Type flows through dictionaries. Generated factories and typed generated helper calls preserve their closed implementations.")]
#endif
        private object ActivateService(Type type, object[]? constructorArguments)
        {
            var result = OrleansGeneratedCodeHelper.TryGetService(type, this);
            if (result != null)
            {
                return result;
            }

            if (TryGetSerializerService(type, out var registered)) return registered;

            if (!IsConstructionPending)
            {
                result = Services.GetService(type);
                if (result != null)
                {
                    return result;
                }
            }

            result = ActivatorUtilities.CreateInstance(Services, type, constructorArguments ?? Array.Empty<object>());
            return result;
        }

        internal bool TryGetSerializerService(Type type, [NotNullWhen(true)] out object? result)
        {
            if (!_manifest.SerializerServiceFactories.TryGetValue(type, out var factory))
            {
                result = null;
                return false;
            }

            if (OrleansGeneratedCodeHelper.TryGetService(type, this) is { } caller)
            {
                result = caller;
                return true;
            }

            result = ConstructService(type, () => factory(this));
            return true;
        }

        private object ConstructService(Type type, Func<object> create, bool beginGraph = true)
        {
            if (beginGraph && !_initialized && !IsInitializationLockHeld)
            {
                if (IsConstructionPending)
                {
                    var error = new InvalidOperationException("Automatic serializer initialization requires a completed construction graph.");
                    RecordConstructionFailure(error);
                    throw error;
                }
                Initialize();
            }
            var current = OrleansGeneratedCodeHelper.GetConstructionScope(this);
            var scope = current ?? new ConstructionScope(this);
            var isRoot = current is null;
            if (isRoot)
            {
                OrleansGeneratedCodeHelper.EnterServiceResolution(this, scope);
            }

            var success = false;
            try
            {
                if (beginGraph) scope.Begin();
                scope.ThrowIfFaulted();
                if (TryGetCached(_serializerServices, type, out var completed))
                {
                    success = true;
                    return completed;
                }
                if (scope.IsPending)
                {
                    if (scope.TryGetService(type, out var pending))
                    {
                        success = true;
                        return pending;
                    }
                    scope.AddService(type, null);
                }

                var result = create();
                scope.ThrowIfFaulted();
                if (scope.IsPending) scope.AddService(type, result);
                success = true;
                return result;
            }
            catch (Exception exception)
            {
                scope.RecordFailure(exception);
                throw;
            }
            finally
            {
                if (isRoot)
                {
                    try
                    {
                        if (success) scope.Publish();
                    }
                    finally
                    {
                        OrleansGeneratedCodeHelper.ExitServiceResolution();
                        scope.Dispose();
                    }
                }
            }
        }

        private bool IsInitializationLockHeld
        {
#if NET9_0_OR_GREATER
            get => _initializationLock.IsHeldByCurrentThread;
#else
            get => Monitor.IsEntered(_initializationLock);
#endif
        }

        private bool TryGetCached<TValue>(ConcurrentDictionary<Type, TValue> cache, Type key, [NotNullWhen(true)] out TValue? result) where TValue : class
        {
            if (_manifest.SerializerServiceFactories.Count == 0) return cache.TryGetValue(key, out result);
            lock (_serializerServices)
            {
                var scope = OrleansGeneratedCodeHelper.GetConstructionScope(this);
                scope?.ThrowIfFaulted();
                if (ReferenceEquals(cache, _serializerServices)
                    && scope?.TryGetService(key, out var service, requireInstance: false) == true)
                {
                    result = (TValue)service;
                    return true;
                }
                if (scope?.TryGetCached(cache, key, out result) == true) return true;
                return cache.TryGetValue(key, out result);
            }
        }

        private TValue CacheValue<TValue>(ConcurrentDictionary<Type, TValue> cache, Type key, TValue value) where TValue : class
        {
            if (_manifest.SerializerServiceFactories.Count == 0) return cache.GetOrAdd(key, value);
            lock (_serializerServices)
            {
                var scope = OrleansGeneratedCodeHelper.GetConstructionScope(this);
                scope?.ThrowIfFaulted();
                if (scope is not { IsPending: true }) return cache.GetOrAdd(key, value);
                if (TryGetCached(cache, key, out var existing)) return existing;
                scope.CacheValue(cache, key, value);
                return value;
            }
        }

        internal bool IsConstructionPending
        {
            get { lock (_serializerServices) return OrleansGeneratedCodeHelper.GetConstructionScope(this) is { IsPending: true }; }
        }

        internal void RecordConstructionFailure(Exception exception)
        {
            OrleansGeneratedCodeHelper.GetConstructionScope(this)?.RecordFailure(exception);
        }

        internal sealed class ConstructionScope(CodecProvider owner) : IDisposable
        {
            private Dictionary<Type, object?>? _services;
            private Dictionary<object, IPendingCache>? _caches;
            private ExceptionDispatchInfo? _failure;
            private bool _lockHeld;

            internal bool IsPending => _lockHeld;

            internal void Begin()
            {
                if (_lockHeld) return;
                Monitor.Enter(owner._serializerServices, ref _lockHeld);
                _services = new();
            }

            internal void ThrowIfFaulted() => _failure?.Throw();

            internal void RecordFailure(Exception exception)
            {
                if (_lockHeld) _failure ??= ExceptionDispatchInfo.Capture(exception);
            }

            internal bool TryGetService(Type type, [NotNullWhen(true)] out object? service, bool requireInstance = true)
            {
                if (_services?.TryGetValue(type, out service) == true)
                {
                    // A null entry marks a factory whose instance has not yet been allocated.
                    if (service is null)
                    {
                        if (!requireInstance) return false;
                        owner.ThrowResolutionFailure(new InvalidOperationException(
                            $"Recursive serializer factory resolution for {type} requires an in-progress instance. Use caller-aware generated-code helpers or statically closed holders to resolve recursive constructor dependencies."));
                    }
                    return true;
                }

                service = null;
                return false;
            }

            internal void AddService(Type type, object? service) => _services![type] = service;

            internal bool TryGetCached<TValue>(ConcurrentDictionary<Type, TValue> cache, Type key, [NotNullWhen(true)] out TValue? value)
                where TValue : class
            {
                if (_caches?.TryGetValue(cache, out var pending) == true)
                    return ((PendingCache<TValue>)pending).Values.TryGetValue(key, out value);
                value = null;
                return false;
            }

            internal void CacheValue<TValue>(ConcurrentDictionary<Type, TValue> cache, Type key, TValue value) where TValue : class
            {
                _caches ??= new();
                if (!_caches.TryGetValue(cache, out var pending))
                    _caches.Add(cache, pending = new PendingCache<TValue>(cache));
                ((PendingCache<TValue>)pending).Values.Add(key, value);
            }

            internal void Publish()
            {
                if (!_lockHeld) return;
                ThrowIfFaulted();
                foreach (var service in _services!) owner._serializerServices.TryAdd(service.Key, service.Value!);
                if (_caches is { } caches)
                    foreach (var cache in caches.Values) cache.Publish();
            }

            public void Dispose()
            {
                _services = null;
                _caches = null;
                _failure = null;
                if (_lockHeld)
                {
                    _lockHeld = false;
                    Monitor.Exit(owner._serializerServices);
                }
            }

            private interface IPendingCache
            {
                void Publish();
            }

            private sealed class PendingCache<TValue>(ConcurrentDictionary<Type, TValue> target) : IPendingCache where TValue : class
            {
                public Dictionary<Type, TValue> Values { get; } = new();
                public void Publish()
                {
                    foreach (var entry in Values) target.TryAdd(entry.Key, entry.Value);
                }
            }
        }

        private object? TryGetConstructedService(Type serviceType)
        {
            if (OrleansGeneratedCodeHelper.TryGetService(serviceType, this) is { } caller) return caller;
            var definition = serviceType.GetGenericTypeDefinition();
            var fieldType = serviceType.GenericTypeArguments[0];
            if (definition == typeof(IFieldCodec<>))
            {
                if (TryGetCached(_typedCodecs, fieldType, out var typed)) return typed;
                if (TryGetCached(_untypedCodecs, fieldType, out var untyped) && serviceType.IsInstanceOfType(untyped)) return untyped;
            }
            else if (definition == typeof(IDeepCopier<>))
            {
                if (TryGetCached(_typedCopiers, fieldType, out var typed)) return typed;
                if (TryGetCached(_untypedCopiers, fieldType, out var untyped) && serviceType.IsInstanceOfType(untyped)) return untyped;
            }
            else if (definition == typeof(IBaseCodec<>))
            {
                if (TryGetCached(_typedBaseCodecs, fieldType, out var typed)) return typed;
            }

            var searchType = fieldType.IsConstructedGenericType ? fieldType.GetGenericTypeDefinition() : fieldType;
            if (!TrySelectImplementation(definition, fieldType, searchType, out var implementation)) return null;
            if (_serializerServices.TryGetValue(implementation, out var completed)) return completed;
            return OrleansGeneratedCodeHelper.GetConstructionScope(this)?.TryGetService(implementation, out var pending, requireInstance: false) == true
                ? pending : null;
        }

        private sealed class ConstructionServiceProvider(CodecProvider owner) : IServiceProvider, IServiceProviderIsService,
            IKeyedServiceProvider, IServiceProviderIsKeyedService
        {
            public object? GetService(Type serviceType)
            {
                lock (owner._serializerServices)
                {
                    var scope = OrleansGeneratedCodeHelper.GetConstructionScope(owner);
                    scope?.ThrowIfFaulted();
                    if (scope is { IsPending: true })
                    {
                        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceProviderIsService)
                            || serviceType == typeof(IServiceProviderIsKeyedService) && owner._serviceProvider is IKeyedServiceProvider) return this;
                        if (IsProviderService(serviceType)) return owner;
                        if (owner._serializerServices.TryGetValue(serviceType, out var completed)) return completed;
                        if (scope.TryGetService(serviceType, out var constructed, requireInstance: false)) return constructed;
                        if (owner.TryGetSerializerService(serviceType, out var registered)) return registered;
                        if (ServiceCollectionExtensions.GetServiceHolderType(serviceType) is { } holderType)
                        {
                            if (owner.TryGetConstructedService(serviceType) is { } existing) return existing;
                            return owner.GetServiceOrCreateInstance(
                                owner.ConstructGenericImplementation(holderType, serviceType.GenericTypeArguments));
                        }
                        if (owner.IsRegisteredImplementation(serviceType))
                            return owner.GetServiceOrCreateInstance(serviceType);
                        var error = new InvalidOperationException($"Dependency injection cannot resolve {serviceType} while a serialization graph is unpublished. Register the dependency through TypeManifestOptions.AddSerializerService using a closed factory which constructs it or returns an explicitly captured instance.");
                        owner.RecordConstructionFailure(error);
                        throw error;
                    }
                }
                return serviceType == typeof(IServiceProvider) ? this : owner._serviceProvider.GetService(serviceType);
            }

            public object? GetKeyedService(Type serviceType, object? serviceKey)
                => ResolveKeyedService(serviceType, serviceKey, required: false);

            public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
                => ResolveKeyedService(serviceType, serviceKey, required: true)!;

            private object? ResolveKeyedService(Type serviceType, object? serviceKey, bool required)
            {
                if (serviceKey is null)
                {
                    var unkeyed = GetService(serviceType);
                    if (unkeyed is null && required)
                        owner.ThrowResolutionFailure(new InvalidOperationException($"No service for type '{serviceType}' has been registered."));
                    return unkeyed;
                }

                lock (owner._serializerServices)
                {
                    var scope = OrleansGeneratedCodeHelper.GetConstructionScope(owner);
                    scope?.ThrowIfFaulted();
                    if (scope is { IsPending: true })
                    {
                        var error = new InvalidOperationException($"Dependency injection cannot resolve keyed service {serviceType} while a serialization graph is unpublished. Supply the keyed dependency through an explicit closed factory registered with TypeManifestOptions.AddSerializerService.");
                        owner.ThrowResolutionFailure(error);
                    }
                }

                if (owner._serviceProvider is not IKeyedServiceProvider keyedProvider)
                    throw new InvalidOperationException("This service provider doesn't support keyed services.");
                return required
                    ? keyedProvider.GetRequiredKeyedService(serviceType, serviceKey)
                    : keyedProvider.GetKeyedService(serviceType, serviceKey);
            }

            public bool IsKeyedService(Type serviceType, object? serviceKey)
            {
                if (serviceKey is null) return IsService(serviceType);
                lock (owner._serializerServices)
                {
                    var scope = OrleansGeneratedCodeHelper.GetConstructionScope(owner);
                    scope?.ThrowIfFaulted();
                    if (scope is { IsPending: true })
                    {
                        return false;
                    }
                }

                return owner._serviceProvider.GetRequiredService<IServiceProviderIsKeyedService>().IsKeyedService(serviceType, serviceKey);
            }

            public bool IsService(Type serviceType)
            {
                lock (owner._serializerServices)
                {
                    var scope = OrleansGeneratedCodeHelper.GetConstructionScope(owner);
                    scope?.ThrowIfFaulted();
                    if (scope is { IsPending: true })
                    {
                        return IsProviderService(serviceType)
                            || serviceType == typeof(IServiceProvider)
                            || serviceType == typeof(IServiceProviderIsService)
                            || serviceType == typeof(IServiceProviderIsKeyedService) && owner._serviceProvider is IKeyedServiceProvider
                            || owner._manifest.SerializerServiceFactories.ContainsKey(serviceType)
                            || owner.IsRegisteredImplementation(serviceType)
                            || ServiceCollectionExtensions.GetServiceHolderType(serviceType) is not null;
                    }
                }

                return owner._serviceProvider.GetRequiredService<IServiceProviderIsService>().IsService(serviceType);
            }

            private bool IsProviderService(Type serviceType)
                => serviceType != typeof(object)
                    && (serviceType == typeof(CodecProvider) || serviceType.IsInterface)
                    && serviceType.IsInstanceOfType(owner);
        }

        [DoesNotReturn]
        private void ThrowResolutionFailure(Exception exception)
        {
            RecordConstructionFailure(exception);
            throw exception;
        }

        private IFieldCodec? CreateCodecInstance(Type fieldType, Type searchType)
        {
            if (searchType == ObjectType)
                return _objectCodec;

            if (TrySelectImplementation(typeof(IFieldCodec<>), fieldType, searchType, out var codecType))
            {
                return (IFieldCodec)GetServiceOrCreateInstance(codecType);
            }

            object[]? constructorArguments = null;
            if (TrySelectImplementation(typeof(IBaseCodec<>), fieldType, searchType, out var baseCodecType))
            {
                // If there is a base type serializer for this type, create a codec which will then accept that base type serializer.
                codecType = ConstructGenericImplementation(typeof(ConcreteTypeSerializer<,>), fieldType, baseCodecType);
                constructorArguments = new[] { GetServiceOrCreateInstance(baseCodecType) };
            }
            else if (TrySelectImplementation(typeof(IValueSerializer<>), fieldType, searchType, out var valueSerializerType))
            {
                // If there is a value serializer for this type, create a codec which will then accept that value serializer.
                codecType = ConstructGenericImplementation(typeof(ValueSerializer<,>), fieldType, valueSerializerType);
                constructorArguments = new[] { GetServiceOrCreateInstance(valueSerializerType) };
            }
            else if (fieldType.IsEnum)
            {
                return CreateCodecInstance(fieldType, fieldType.GetEnumUnderlyingType());
            }
            else if (TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments))
            {
                codecType = surrogateCodecType;
            }
            else if (fieldType.IsArray)
            {
                // Depending on the type of the array, select the base array codec or the multi-dimensional codec.
                codecType = fieldType.IsSZArray
                    ? ConstructGenericImplementation(typeof(ArrayCodec<>), fieldType.GetElementType()!)
                    : ConstructGenericImplementation(typeof(MultiDimensionalArrayCodec<,>), fieldType, fieldType.GetElementType()!);
            }
            else if (searchType.BaseType is object
                && CreateCodecInstance(
                    fieldType.BaseType!,
                    searchType.BaseType switch
                    {
                        { IsConstructedGenericType: true } => searchType.BaseType.GetGenericTypeDefinition(),
                        _ => searchType.BaseType
                    }) is IDerivedTypeCodec fieldCodec)
            {
                // Find codecs which generalize over all subtypes.
                return fieldCodec;
            }

            return codecType != null ? (IFieldCodec)GetServiceOrCreateInstance(codecType, constructorArguments) : null;
        }

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = "Legacy converter registrations preserve implementation interfaces through AddConverter(Type). The selected implementation boundary erases that annotation; explicit contracts use their stored surrogate descriptions instead.")]
        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = "Legacy converter registrations preserve implementation interfaces through AddConverter(Type). Materialization erases the registration annotation; explicit contracts use their stored surrogate descriptions instead.")]
#endif
        private bool TryGetSurrogateCodec(Type fieldType, Type searchType, [NotNullWhen(true)] out Type? surrogateCodecType, [NotNullWhen(true)] out object[]? constructorArguments)
        {
            if (TrySelectImplementation(typeof(IConverter<,>), fieldType, searchType, out var converterType, out var registration))
            {
                Type? surrogate = null;
                if (registration.RegistrationOrder is not null)
                {
                    var arguments = converterType.IsGenericType ? converterType.GetGenericArguments() : Array.Empty<Type>();
                    surrogate = registration.SurrogateDescription is { } description
                        ? ResolveSerializationType(description, arguments)
                        : registration.SurrogateType!;
                    if (surrogate.IsGenericTypeDefinition)
                    {
                        surrogate = ConstructGenericImplementation(surrogate, arguments);
                    }
                }
                else
                {
                    foreach (var @interface in converterType.GetInterfaces())
                    {
                        if (@interface.IsConstructedGenericType && @interface.GetGenericTypeDefinition() == typeof(IConverter<,>)
                            && @interface.GenericTypeArguments[0] == fieldType)
                        {
                            surrogate = @interface.GenericTypeArguments[1];
                        }
                    }
                }

                if (surrogate is null)
                {
                    throw new InvalidOperationException($"A registered type converter {converterType} does not implement {typeof(IConverter<,>)}");
                }

                constructorArguments = new object[] { GetServiceOrCreateInstance(converterType) };
                surrogateCodecType = ConstructGenericImplementation(
                    fieldType.IsValueType ? typeof(ValueTypeSurrogateCodec<,,>) : typeof(SurrogateCodec<,,>),
                    fieldType, surrogate, converterType);

                return true;
            }

            surrogateCodecType = null;
            constructorArguments = null;
            return false;
        }

        private bool TrySelectImplementation(
            Type contractType,
            Type targetType,
            Type searchType,
            [NotNullWhen(true)] out Type? implementation)
            => TrySelectImplementation(contractType, targetType, searchType, out implementation, out _);

        private bool TrySelectImplementation(
            Type contractType,
            Type targetType,
            Type searchType,
            [NotNullWhen(true)] out Type? implementation,
            out SerializationContract registration)
        {
            if (targetType != searchType && TrySelect(targetType, out implementation, out registration))
            {
                return true;
            }

            return TrySelect(searchType, out implementation, out registration) || TrySelect(null, out implementation, out registration);

            bool TrySelect(Type? key, [NotNullWhen(true)] out Type? result, out SerializationContract selected)
            {
                if (_implementationCandidates.TryGetValue((contractType, key), out var candidates))
                {
                    for (var i = candidates.Count - 1; i >= 0; i--)
                    {
                        var candidate = candidates[i];
                        Type?[]? bindings = null;
                        if (candidate.Contract.TargetDescription is { } description)
                        {
                            bindings = new Type?[candidate.Implementation.GetGenericArguments().Length];
                            if (!BindTypeArguments(description, targetType, bindings))
                            {
                                continue;
                            }
                        }

                        var closed = candidate.Implementation;
                        if (closed.IsGenericTypeDefinition)
                        {
                            var arguments = bindings is null ? targetType.GetGenericArguments() : new Type[bindings.Length];
                            if (bindings is not null)
                            {
                                for (var argument = 0; argument < arguments.Length; argument++)
                                {
                                    arguments[argument] = bindings[argument] ?? throw new InvalidOperationException(
                                        $"Serialization contract for {candidate.Implementation} does not bind generic parameter {argument} from target {targetType}.");
                                }
                            }

                            if (arguments.Length != candidate.Implementation.GetGenericArguments().Length)
                            {
                                closed = ConstructGenericImplementation(candidate.Implementation, arguments);
                            }
                            else
                            {
                                try
                                {
                                    closed = MaterializeGenericImplementation(candidate.Implementation, arguments);
                                }
                                catch (ArgumentException)
                                {
                                    // The runtime rejected the bound arguments, including generic constraints.
                                    continue;
                                }
                            }
                        }

                        result = closed;
                        selected = candidate.Contract;
                        return true;
                    }
                }

                result = null;
                selected = default;
                return false;
            }
        }

        private static bool BindTypeArguments(SerializationType description, Type target, Type?[] arguments)
        {
            if (description.ParameterIndex >= 0)
            {
                return BindParameter(description.ParameterIndex, target, arguments);
            }

            if (description.ArrayRank > 0)
            {
                return target.IsArray && target.GetArrayRank() == description.ArrayRank
                    && (description.ArrayRank != 1 || target.IsSZArray)
                    && BindTypeArguments(description.Arguments[0], target.GetElementType()!, arguments);
            }

            if (description.Arguments.Length == 0 && description.Type is not { IsGenericTypeDefinition: true })
            {
                return description.Type == target;
            }

            if (!target.IsConstructedGenericType || target.GetGenericTypeDefinition() != description.Type)
            {
                return false;
            }

            var parameters = target.GetGenericArguments();
            if (description.Arguments.Length == 0 && parameters.Length != arguments.Length)
            {
                throw new InvalidOperationException(
                    $"Serialization contract generic definition {description.Type} has arity {parameters.Length}, but implementation arity is {arguments.Length}. Supply explicit generic argument descriptions.");
            }

            for (var i = 0; i < parameters.Length; i++)
            {
                if (!(description.Arguments.Length == 0
                    ? BindParameter(i, parameters[i], arguments)
                    : BindTypeArguments(description.Arguments[i], parameters[i], arguments)))
                {
                    return false;
                }
            }

            return true;

            static bool BindParameter(int index, Type target, Type?[] arguments)
            {
                if (index >= arguments.Length)
                {
                    throw new InvalidOperationException($"Serialization contract parameter {index} exceeds implementation arity {arguments.Length}.");
                }

                if (arguments[index] is { } existing)
                {
                    return existing == target;
                }

                arguments[index] = target;
                return true;
            }
        }

        private Type ResolveSerializationType(SerializationType description, Type[] parameters)
        {
            if (description.ParameterIndex >= 0)
            {
                if (description.ParameterIndex >= parameters.Length)
                {
                    throw new InvalidOperationException($"Surrogate generic parameter {description.ParameterIndex} exceeds the converter's arity {parameters.Length}.");
                }

                return parameters[description.ParameterIndex];
            }

            if (description.ArrayRank > 0)
            {
                throw new NotSupportedException(
                    "Supply a source-known closed array type using SerializationType.Create(typeof(ClosedArray)) or an explicit closed converter registration when resolving executable serialization metadata. Array descriptions are structural matching patterns.");
            }

            var type = description.Type!;
            if (description.Arguments.Length == 0)
            {
                return type.IsGenericTypeDefinition ? ConstructGenericImplementation(type, parameters) : type;
            }

            var arguments = new Type[description.Arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                arguments[i] = ResolveSerializationType(description.Arguments[i], parameters);
            }

            return ConstructGenericImplementation(type, arguments);
        }

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage("AOT", "IL3050",
            Justification = "Generated closed factories take priority. Metadata-based resolution can materialize a registered implementation whose closed native code is already rooted; unsupported native instantiations fail with registration guidance.")]
        [UnconditionalSuppressMessage("Trimming", "IL2055",
            Justification = "Registered definitions preserve constructors through manifest APIs and interface metadata through SerializationType.Create. Closed factories or typed generated dependencies preserve native instantiations; arbitrary unrooted shapes require an explicit registration.")]
#endif
        private static Type MaterializeGenericImplementation(Type implementation, params Type[] arguments)
        {
            try
            {
                return implementation.MakeGenericType(arguments);
            }
            catch (NotSupportedException exception) when (!RuntimeFeature.IsDynamicCodeSupported)
            {
                throw new NotSupportedException(
                    $"The runtime cannot materialize serialization implementation {implementation} for [{string.Join<Type>(", ", arguments)}]. Register its closed codec/copier and dependencies using a serializer context or closed factories.",
                    exception);
            }
        }

        private IBaseCodec? CreateBaseCodecInstance(Type fieldType, Type searchType)
        {
            object[]? constructorArguments = null;
            if (!TrySelectImplementation(typeof(IBaseCodec<>), fieldType, searchType, out var codecType)
                && TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments)
                && typeof(IBaseCodec).IsAssignableFrom(surrogateCodecType))
            {
                codecType = surrogateCodecType;
            }

            return codecType != null ? (IBaseCodec)GetServiceOrCreateInstance(codecType, constructorArguments) : null;
        }

        private IDeepCopier? CreateCopierInstance(Type fieldType, Type searchType)
        {
            if (searchType == ObjectType)
                return _objectCopier;

            if (TrySelectImplementation(typeof(IDeepCopier<>), fieldType, searchType, out var copierType))
            {
                return (IDeepCopier)GetServiceOrCreateInstance(copierType);
            }

            object[]? constructorArguments = null;
            if (ShallowCopyableTypes.Contains(fieldType))
            {
                return ShallowCopier.Instance;
            }
            else if (TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments))
            {
                copierType = surrogateCodecType;
            }
            else if (fieldType.IsArray)
            {
                // Depending on the type of the array, select the base array copier or the multi-dimensional copier.
                var arrayCopierType = fieldType.IsSZArray ? typeof(ArrayCopier<>) : typeof(MultiDimensionalArrayCopier<>);
                copierType = ConstructGenericImplementation(arrayCopierType, fieldType.GetElementType()!);
            }
            else if (searchType.BaseType is { } baseType
                && CreateCopierInstance(
                    fieldType.BaseType!,
                    baseType switch
                    {
                        { IsConstructedGenericType: true } => baseType.GetGenericTypeDefinition(),
                        _ => baseType
                    }) is IDerivedTypeCopier derivedTypeCopier)
            {
                // Find copiers which generalize over all subtypes. The field type and search type are advanced in
                // lockstep so that the generic arguments used to construct the copier always match the arity of the
                // copier's declared type, even when a subtype changes arity (e.g. the internal frozen collection types).
                return derivedTypeCopier;
            }

            return copierType != null ? (IDeepCopier)GetServiceOrCreateInstance(copierType, constructorArguments) : null;
        }

        private Type ConstructGenericImplementation(Type implementation, params Type[] arguments)
        {
            try
            {
                return MaterializeGenericImplementation(implementation, arguments);
            }
            catch (Exception exception)
            {
                RecordConstructionFailure(exception);
                throw;
            }
        }

        [DoesNotReturn]
        private static void ThrowPointerType(Type fieldType) => throw new NotSupportedException($"Type {fieldType} is a pointer type and is therefore not supported.");

        [DoesNotReturn]
        private static void ThrowByRefType(Type fieldType) => throw new NotSupportedException($"Type {fieldType} is a by-ref type and is therefore not supported.");

        [DoesNotReturn]
        private static void ThrowGenericTypeDefinition(Type fieldType) => throw new InvalidOperationException($"Type {fieldType} is a non-constructed generic type and is therefore unsupported.");

        [DoesNotReturn]
        private void ThrowCodecNotFound(Type fieldType) => ThrowResolutionFailure(new CodecNotFoundException($"Could not find a codec for type {fieldType}."));

        [DoesNotReturn]
        private void ThrowCopierNotFound(Type type) => ThrowResolutionFailure(new CodecNotFoundException($"Could not find a copier for type {type}."));

        [DoesNotReturn]
        private void ThrowBaseCodecNotFound(Type fieldType) => ThrowResolutionFailure(new KeyNotFoundException($"Could not find a base type serializer for type {fieldType}."));

        [DoesNotReturn]
        private void ThrowValueSerializerNotFound(Type fieldType) => ThrowResolutionFailure(new KeyNotFoundException($"Could not find a value serializer for type {fieldType}."));

        [DoesNotReturn]
        private void ThrowActivatorNotFound(Type type) => ThrowResolutionFailure(new KeyNotFoundException($"Could not find an activator for type {type}."));

        [DoesNotReturn]
        private void ThrowBaseCopierNotFound(Type type) => ThrowResolutionFailure(new KeyNotFoundException($"Could not find a base type copier for type {type}."));
    }
}
