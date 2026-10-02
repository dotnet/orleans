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

        private readonly ConcurrentDictionary<Type, object> _instantiatedBaseCopiers = new();
        private readonly ConcurrentDictionary<Type, object> _instantiatedValueSerializers = new();
        private readonly ConcurrentDictionary<Type, object> _instantiatedActivators = new();
        private readonly Dictionary<Type, Type> _baseCodecs = new();
        private readonly Dictionary<Type, Type> _valueSerializers = new();
        private readonly Dictionary<Type, Type> _fieldCodecs = new();
        private readonly Dictionary<Type, Type> _copiers = new();
        private readonly Dictionary<Type, Type> _converters = new();
        private readonly Dictionary<Type, SerializationContract> _converterContracts = new();
        private readonly Dictionary<Type, Type> _baseCopiers = new();
        private readonly Dictionary<Type, Type> _activators = new();
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
        private readonly object _serializerServiceLock = new();
        private readonly Dictionary<Type, object> _serializerServices = new();
        private Dictionary<Type, object>? _pendingSerializerServices;
        private Dictionary<(object Cache, Type Key), object>? _pendingLegacyCacheValues;
        private Dictionary<(object Cache, Type Key), Action>? _pendingLegacyCacheCommits;
        private readonly List<Type> _constructingSerializerServices = new();
        private ExceptionDispatchInfo? _constructionFailure;
        private readonly List<(object Caller, object Dependency)> _constructionDependencies = new();
        private int _initializingThreadId;
        private readonly IServiceProvider _constructionServices;
        private readonly IReadOnlyList<ServiceDescriptor> _serviceDescriptors;
        private bool _initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="CodecProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="codecConfiguration">The codec configuration.</param>
        public CodecProvider(IServiceProvider serviceProvider, IOptions<TypeManifestOptions> codecConfiguration)
        {
            _serviceProvider = serviceProvider;
            _serviceDescriptors = ServiceCollectionExtensions.GetServiceDescriptors(serviceProvider);
            _constructionServices = new ConstructionServiceProvider(this);
            _manifest = codecConfiguration.Value;
            ConsumeMetadata(codecConfiguration);
        }

        /// <inheritdoc/>
        public IServiceProvider Services => _manifest.SerializerServiceFactories.Count == 0 ? _serviceProvider : _constructionServices;

        /// <summary>
        /// Resolves a statically registered reader for a raw invocation result.
        /// </summary>
        /// <param name="resultType">The closed result type from the message field header.</param>
        /// <param name="reader">The registered reader, when supported by the selected response codec.</param>
        /// <returns>Whether a compatible registered reader is available.</returns>
        public bool TryGetRawResponseReader(Type resultType, [NotNullWhen(true)] out Invocation.IRawResponseReader? reader)
        {
            if (resultType is null) throw new ArgumentNullException(nameof(resultType));
            if (_manifest.RawResponseReaderFactories.TryGetValue(resultType, out var factory))
            {
                var candidate = factory(this);
                if (candidate.IsSupported)
                {
                    reader = candidate;
                    return true;
                }
            }

            reader = null;
            return false;
        }

        private void Initialize()
        {
            lock (_initializationLock)
            {
                if (_initialized)
                {
                    return;
                }

                _initializingThreadId = Environment.CurrentManagedThreadId;
                try
                {
                    _generalizedCodecs.AddRange(_serviceProvider.GetServices<IGeneralizedCodec>());
                    _generalizedBaseCodecs.AddRange(_serviceProvider.GetServices<IGeneralizedBaseCodec>());
                    _generalizedCopiers.AddRange(_serviceProvider.GetServices<IGeneralizedCopier>());

                    _specializableCodecs.AddRange(_serviceProvider.GetServices<ISpecializableCodec>());
                    _specializableCopiers.AddRange(_serviceProvider.GetServices<ISpecializableCopier>());
                    _specializableBaseCodecs.AddRange(_serviceProvider.GetServices<ISpecializableBaseCodec>());

                    _initialized = true;
                }
                finally
                {
                    _initializingThreadId = 0;
                }
            }
        }

        private void ConsumeMetadata(IOptions<TypeManifestOptions> codecConfiguration)
        {
            var metadata = codecConfiguration.Value;
            AddFromMetadata(_baseCodecs, metadata.SerializerTypes, metadata.SerializerContracts, typeof(IBaseCodec<>));
            AddFromMetadata(_valueSerializers, metadata.SerializerTypes, metadata.SerializerContracts, typeof(IValueSerializer<>));
            AddFromMetadata(_fieldCodecs, metadata.SerializerTypes, metadata.SerializerContracts, typeof(IFieldCodec<>));
            AddFromMetadata(_fieldCodecs, metadata.FieldCodecTypes, metadata.SerializerContracts, typeof(IFieldCodec<>));
            AddFromMetadata(_activators, metadata.ActivatorTypes, metadata.ActivatorContracts, typeof(IActivator<>));
            AddFromMetadata(_copiers, metadata.CopierTypes, metadata.CopierContracts, typeof(IDeepCopier<>));
            AddFromMetadata(_converters, metadata.ConverterTypes, metadata.ConverterContracts, typeof(IConverter<,>));
            AddFromMetadata(_baseCopiers, metadata.CopierTypes, metadata.CopierContracts, typeof(IBaseCopier<>));

#if NET5_0_OR_GREATER
            [UnconditionalSuppressMessage(
                "Trimming",
                "IL2075",
                Justification = "Legacy implementation-only registrations preserve implemented interfaces through annotated TypeManifestOptions methods. Explicit contract registrations are consumed directly. The HashSet<Type> boundary cannot retain the legacy annotations.")]
#endif
            void AddFromMetadata(
                Dictionary<Type, Type> resultCollection,
                HashSet<Type> metadataCollection,
                Dictionary<Type, List<SerializationContract>> contracts,
                Type genericType)
            {
                Debug.Assert(genericType.GetGenericArguments().Length >= 1);

                foreach (var type in metadataCollection)
                {
                    if (contracts.TryGetValue(type, out var registrations))
                    {
                        foreach (var registration in registrations)
                        {
                            if (registration.ContractType != genericType)
                            {
                                continue;
                            }

                            var target = registration.TargetDescription is { } description
                                ? ResolveSerializationType(description, type.GetGenericArguments())
                                : registration.TargetType!;
                            if (target != typeof(object))
                            {
                                resultCollection[target] = type;
                                if (genericType == typeof(IConverter<,>))
                                {
                                    _converterContracts[target] = registration;
                                }
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

                        if (genericArgument.IsConstructedGenericType && Array.Exists(genericArgument.GenericTypeArguments, arg => arg.IsGenericParameter))
                        {
                            genericArgument = genericArgument.GetGenericTypeDefinition();
                        }

                        if (registrations is not null && registrations.Exists(registration =>
                            registration.ContractType == genericType
                            && (registration.TargetDescription is { } description
                                ? ResolveSerializationType(description, type.GetGenericArguments())
                                : registration.TargetType) == genericArgument))
                        {
                            continue;
                        }

                        resultCollection[genericArgument] = type;
                        if (genericType == typeof(IConverter<,>))
                        {
                            _converterContracts.Remove(genericArgument);
                        }
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
            if (_valueSerializers.TryGetValue(searchType, out var serializerType))
            {
                if (serializerType.IsGenericTypeDefinition)
                {
                    serializerType = ConstructGenericImplementation(serializerType, concreteType.GetGenericArguments());
                }
            }
            else if (TryGetSurrogateCodec(concreteType, searchType, out var surrogateCodecType, out constructorArguments) && typeof(IValueSerializer).IsAssignableFrom(surrogateCodecType))
            {
                serializerType = surrogateCodecType;
            }
            else
            {
                return null;
            }

            if (!TryGetCached(_instantiatedValueSerializers, serializerType, out var result))
            {
                result = CacheValue(_instantiatedValueSerializers, serializerType, GetServiceOrCreateInstance(serializerType, constructorArguments));
            }

            return result;
        }

        private object? GetBaseCopierInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            object[]? constructorArguments = null;
            if (_baseCopiers.TryGetValue(searchType, out var copierType))
            {
                // Use the detected copier type.
                if (copierType.IsGenericTypeDefinition)
                {
                    copierType = ConstructGenericImplementation(copierType, concreteType.GetGenericArguments());
                }
            }
            else if (TryGetSurrogateCodec(concreteType, searchType, out var surrogateCodecType, out constructorArguments) && typeof(IBaseCopier).IsAssignableFrom(surrogateCodecType))
            {
                copierType = surrogateCodecType;
            }
            else
            {
                return null;
            }

            if (!TryGetCached(_instantiatedBaseCopiers, copierType, out var result))
            {
                result = CacheValue(_instantiatedBaseCopiers, copierType, GetServiceOrCreateInstance(copierType, constructorArguments));
            }

            return result;
        }

        private object GetActivatorInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            if (!_activators.TryGetValue(searchType, out var activatorType))
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
            else if (activatorType.IsGenericTypeDefinition)
            {
                activatorType = ConstructGenericImplementation(activatorType, concreteType.GetGenericArguments());
            }

            if (!TryGetCached(_instantiatedActivators, activatorType, out var result))
            {
                result = CacheValue(_instantiatedActivators, activatorType, GetServiceOrCreateInstance(activatorType));
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
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(SurrogateCodec<,,>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ValueTypeSurrogateCodec<,,>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ArrayCopier<>))]
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2067",
            Justification = "Generated manifests and trim-safe manual configuration use the annotated TypeManifestOptions registration methods, which preserve public constructors. Built-in dynamically closed implementations are rooted by DynamicDependency attributes. Other implementation types are resolved from dependency injection before this activation path. The TypeManifestOptions and dictionary boundaries cannot retain the annotations.")]
#endif
        private object GetServiceOrCreateInstance(Type type, object[]? constructorArguments = null)
        {
            try { return ActivateService(type, constructorArguments); }
            catch (Exception exception) { RecordConstructionFailure(exception); throw; }
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

            result = Services.GetService(type);
            if (result != null)
            {
                return result;
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

            if (!_initialized
                && _initializingThreadId != Environment.CurrentManagedThreadId)
            {
                if (Monitor.IsEntered(_serializerServiceLock) && _pendingSerializerServices is not null)
                {
                    var error = new InvalidOperationException("Automatic serializer initialization requires a completed construction graph.");
                    RecordConstructionFailure(error);
                    throw error;
                }
                Initialize();
            }
            lock (_serializerServiceLock)
            {
                _constructionFailure?.Throw();
                if (_serializerServices.TryGetValue(type, out result)) return true;
                if (_pendingSerializerServices?.TryGetValue(type, out result) == true) return true;
                var isRoot = _pendingSerializerServices is null;
                _pendingSerializerServices ??= new();
                _constructingSerializerServices.Add(type);
                try
                {
                    result = factory(this);
                    _constructionFailure?.Throw();
                    _pendingSerializerServices.Add(type, result);
                    if (isRoot)
                    {
                        foreach (var entry in _pendingSerializerServices)
                        {
                            _serializerServices.Add(entry.Key, entry.Value);
                        }

                        if (_pendingLegacyCacheCommits is { } commits)
                        {
                            foreach (var commit in commits.Values) commit();
                        }
                    }

                    return true;
                }
                catch (Exception exception)
                {
                    _constructionFailure ??= ExceptionDispatchInfo.Capture(exception);
                    throw;
                }
                finally
                {
                    _constructingSerializerServices.RemoveAt(_constructingSerializerServices.Count - 1);
                    if (isRoot)
                    {
                        _pendingSerializerServices = null;
                        _pendingLegacyCacheValues = null;
                        _pendingLegacyCacheCommits = null;
                        _constructionFailure = null;
                        _constructionDependencies.Clear();
                    }
                }
            }
        }

        private bool TryGetCached<TValue>(ConcurrentDictionary<Type, TValue> cache, Type key, [NotNullWhen(true)] out TValue? result) where TValue : class
        {
            if (_manifest.SerializerServiceFactories.Count == 0) return cache.TryGetValue(key, out result);
            lock (_serializerServiceLock)
            {
                _constructionFailure?.Throw();
                if (_pendingLegacyCacheValues?.TryGetValue((cache, key), out var pending) == true)
                {
                    result = (TValue)pending;
                    return true;
                }

                return cache.TryGetValue(key, out result);
            }
        }

        private TValue CacheValue<TValue>(ConcurrentDictionary<Type, TValue> cache, Type key, TValue value) where TValue : class
        {
            if (_manifest.SerializerServiceFactories.Count == 0) return cache.GetOrAdd(key, value);
            lock (_serializerServiceLock)
            {
                _constructionFailure?.Throw();
                if (_pendingSerializerServices is null) return cache.GetOrAdd(key, value);
                if (TryGetCached(cache, key, out var existing)) return existing;
                (_pendingLegacyCacheValues ??= new())[(cache, key)] = value;
                (_pendingLegacyCacheCommits ??= new())[(cache, key)] = () => cache.GetOrAdd(key, value);
                return value;
            }
        }

        internal void RecordConstructionDependency(IReadOnlyList<object> callers, object dependency)
        {
            lock (_serializerServiceLock)
            {
                _constructionFailure?.Throw();
                if (_pendingSerializerServices is null || callers.Count == 0) return;
                for (var index = 1; index < callers.Count; index++)
                {
                    AddEdge(callers[index - 1], callers[index]);
                }

                var caller = callers[^1];
                AddEdge(caller, dependency);
                var cycle = new List<object>();
                if (!FindPath(dependency, caller, new List<object>(), cycle)) return;
                var hasStatic = false;
                var hasAutomatic = false;
                foreach (var node in cycle)
                {
                    var isStatic = _constructingSerializerServices.Exists(type => type.IsInstanceOfType(node))
                        || _pendingSerializerServices.Values.Any(value => ReferenceEquals(value, node));
                    hasStatic |= isStatic;
                    hasAutomatic |= !isStatic;
                }

                if (hasStatic && hasAutomatic)
                {
                    throw new InvalidOperationException("Cyclic construction combines closed serializer factories and automatic activation. Register closed factories for every serialization service in this cycle.");
                }
            }

            void AddEdge(object caller, object dependency)
            {
                if (!_constructionDependencies.Any(edge => ReferenceEquals(edge.Caller, caller) && ReferenceEquals(edge.Dependency, dependency)))
                    _constructionDependencies.Add((caller, dependency));
            }

            bool FindPath(object source, object target, List<object> visited, List<object> path)
            {
                if (visited.Any(node => ReferenceEquals(node, source))) return false;
                visited.Add(source);
                path.Add(source);
                if (ReferenceEquals(source, target)) return true;
                foreach (var edge in _constructionDependencies)
                {
                    if (ReferenceEquals(edge.Caller, source) && FindPath(edge.Dependency, target, visited, path)) return true;
                }
                path.RemoveAt(path.Count - 1);
                return false;
            }
        }

        internal bool IsConstructionPending
        {
            get { lock (_serializerServiceLock) return _pendingSerializerServices is not null; }
        }

        internal void RecordConstructionFailure(Exception exception)
        {
            lock (_serializerServiceLock)
            {
                if (_pendingSerializerServices is not null)
                    _constructionFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        private sealed class ConstructionServiceProvider(CodecProvider owner) : IServiceProvider, IServiceProviderIsService,
            IKeyedServiceProvider, IServiceProviderIsKeyedService
        {
            public object? GetService(Type serviceType)
            {
                lock (owner._serializerServiceLock)
                {
                    owner._constructionFailure?.Throw();
                    if (owner._pendingSerializerServices is not null)
                    {
                        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceProviderIsService)
                            || serviceType == typeof(IServiceProviderIsKeyedService) && owner._serviceProvider is IKeyedServiceProvider) return this;
                        if (IsProviderService(serviceType)) return owner;
                        var descriptor = owner._serviceDescriptors.LastOrDefault(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == serviceType)
                            ?? owner._serviceDescriptors.LastOrDefault(descriptor => !descriptor.IsKeyedService && serviceType.IsConstructedGenericType
                                && descriptor.ServiceType == serviceType.GetGenericTypeDefinition());
                        if (descriptor?.ImplementationInstance is { } instance) return instance;
                        if (owner.TryGetSerializerService(serviceType, out var registered)) return registered;
                        if (descriptor is null) return null;
                        var error = new InvalidOperationException($"Dependency injection cannot resolve {serviceType} while a serialization graph is unpublished. Register a closed service factory or provide an instance registration.");
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

                lock (owner._serializerServiceLock)
                {
                    owner._constructionFailure?.Throw();
                    if (owner._pendingSerializerServices is not null)
                    {
                        var descriptor = owner._serviceDescriptors.LastOrDefault(descriptor => descriptor.IsKeyedService
                            && Equals(descriptor.ServiceKey, serviceKey) && descriptor.ServiceType == serviceType)
                            ?? owner._serviceDescriptors.LastOrDefault(descriptor => descriptor.IsKeyedService
                                && Equals(descriptor.ServiceKey, serviceKey) && serviceType.IsConstructedGenericType
                                && descriptor.ServiceType == serviceType.GetGenericTypeDefinition())
                            ?? owner._serviceDescriptors.LastOrDefault(descriptor => descriptor.IsKeyedService
                                && Equals(descriptor.ServiceKey, KeyedService.AnyKey) && descriptor.ServiceType == serviceType)
                            ?? owner._serviceDescriptors.LastOrDefault(descriptor => descriptor.IsKeyedService
                                && Equals(descriptor.ServiceKey, KeyedService.AnyKey) && serviceType.IsConstructedGenericType
                                && descriptor.ServiceType == serviceType.GetGenericTypeDefinition());
                        if (descriptor?.KeyedImplementationInstance is { } instance) return instance;
                        if (descriptor is null && !required) return null;
                        var error = new InvalidOperationException($"Dependency injection cannot resolve keyed service {serviceType} while a serialization graph is unpublished. Provide an explicit keyed instance registration.");
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
                lock (owner._serializerServiceLock)
                {
                    owner._constructionFailure?.Throw();
                    if (owner._pendingSerializerServices is not null)
                    {
                        return owner._serviceDescriptors.Any(descriptor => descriptor.IsKeyedService
                            && (Equals(descriptor.ServiceKey, serviceKey) || Equals(descriptor.ServiceKey, KeyedService.AnyKey))
                            && (descriptor.ServiceType == serviceType
                                || serviceType.IsConstructedGenericType && descriptor.ServiceType == serviceType.GetGenericTypeDefinition()));
                    }
                }

                return owner._serviceProvider.GetRequiredService<IServiceProviderIsKeyedService>().IsKeyedService(serviceType, serviceKey);
            }

            public bool IsService(Type serviceType)
                => IsProviderService(serviceType)
                    || serviceType == typeof(IServiceProvider)
                    || serviceType == typeof(IServiceProviderIsService)
                    || serviceType == typeof(IServiceProviderIsKeyedService) && owner._serviceProvider is IKeyedServiceProvider
                    || owner._manifest.SerializerServiceFactories.ContainsKey(serviceType)
                    || owner._serviceDescriptors.Any(descriptor => !descriptor.IsKeyedService
                        && (descriptor.ServiceType == serviceType
                            || serviceType.IsConstructedGenericType && descriptor.ServiceType == serviceType.GetGenericTypeDefinition()));

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

            object[]? constructorArguments = null;
            if (_fieldCodecs.TryGetValue(searchType, out var codecType))
            {
                if (codecType.IsGenericTypeDefinition)
                {
                    codecType = ConstructGenericImplementation(codecType, fieldType.GetGenericArguments());
                }
            }
            else if (_baseCodecs.TryGetValue(searchType, out var baseCodecType))
            {
                if (baseCodecType.IsGenericTypeDefinition)
                {
                    baseCodecType = ConstructGenericImplementation(baseCodecType, fieldType.GetGenericArguments());
                }

                // If there is a base type serializer for this type, create a codec which will then accept that base type serializer.
                codecType = ConstructGenericImplementation(typeof(ConcreteTypeSerializer<,>), fieldType, baseCodecType);
                constructorArguments = new[] { GetServiceOrCreateInstance(baseCodecType) };
            }
            else if (_valueSerializers.TryGetValue(searchType, out var valueSerializerType))
            {
                if (valueSerializerType.IsGenericTypeDefinition)
                {
                    valueSerializerType = ConstructGenericImplementation(valueSerializerType, fieldType.GetGenericArguments());
                }

                // If there is a value serializer for this type, create a codec which will then accept that value serializer.
                codecType = ConstructGenericImplementation(typeof(ValueSerializer<,>), fieldType, valueSerializerType);
                constructorArguments = new[] { GetServiceOrCreateInstance(valueSerializerType) };
            }
            else if (fieldType.IsArray)
            {
                // Depending on the type of the array, select the base array codec or the multi-dimensional codec.
                var arrayCodecType = fieldType.IsSZArray ? typeof(ArrayCodec<>) : typeof(MultiDimensionalArrayCodec<>);
                codecType = ConstructGenericImplementation(arrayCodecType, fieldType.GetElementType()!);
            }
            else if (fieldType.IsEnum)
            {
                return CreateCodecInstance(fieldType, fieldType.GetEnumUnderlyingType());
            }
            else if (TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments))
            {
                // Use the converter
                codecType = surrogateCodecType;
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
            Justification = "Converter types come from AddConverter registrations which preserve implemented interfaces. The implementation dictionary erases that annotation.")]
        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = "Closed converters use the interfaces preserved by their AddConverter registration. Runtime generic materialization erases the registration annotation.")]
#endif
        private bool TryGetSurrogateCodec(Type fieldType, Type searchType, [NotNullWhen(true)] out Type? surrogateCodecType, [NotNullWhen(true)] out object[]? constructorArguments)
        {
            if (_converters.TryGetValue(searchType, out var converterType))
            {
                if (converterType.IsGenericTypeDefinition)
                {
                    converterType = ConstructGenericImplementation(converterType, fieldType.GetGenericArguments());
                }

                var converterInterfaceArgs = Array.Empty<Type>();
                if (_converterContracts.TryGetValue(searchType, out var registration))
                {
                    var arguments = converterType.IsGenericType ? converterType.GetGenericArguments() : Array.Empty<Type>();
                    var surrogate = registration.SurrogateDescription is { } description
                        ? ResolveSerializationType(description, arguments)
                        : registration.SurrogateType!;
                    if (surrogate.IsGenericTypeDefinition)
                    {
                        surrogate = surrogate.MakeGenericType(arguments);
                    }

                    converterInterfaceArgs = [fieldType, surrogate];
                }
                else
                {
                    foreach (var @interface in converterType.GetInterfaces())
                    {
                        if (@interface.IsConstructedGenericType && @interface.GetGenericTypeDefinition() == typeof(IConverter<,>)
                            && @interface.GenericTypeArguments[0] == fieldType)
                        {
                            converterInterfaceArgs = @interface.GetGenericArguments();
                        }
                    }
                }

                if (converterInterfaceArgs is { Length: 0 })
                {
                    throw new InvalidOperationException($"A registered type converter {converterType} does not implement {typeof(IConverter<,>)}");
                }

                var typeArgs = new Type[3] { converterInterfaceArgs[0], converterInterfaceArgs[1], converterType };
                constructorArguments = new object[] { GetServiceOrCreateInstance(converterType) };
                if (typeArgs[0].IsValueType)
                {
                    surrogateCodecType = ConstructGenericImplementation(typeof(ValueTypeSurrogateCodec<,,>), typeArgs);
                }
                else
                {
                    surrogateCodecType = ConstructGenericImplementation(typeof(SurrogateCodec<,,>), typeArgs);
                }

                return true;
            }

            surrogateCodecType = null;
            constructorArguments = null;
            return false;
        }

        private static Type ResolveSerializationType(SerializationType description, Type[] parameters)
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
                var element = ResolveSerializationType(description.Arguments[0], parameters);
                return description.ArrayRank == 1 ? element.MakeArrayType() : element.MakeArrayType(description.ArrayRank);
            }

            var type = description.Type!;
            if (description.Arguments.Length == 0)
            {
                return type.IsGenericTypeDefinition ? type.MakeGenericType(parameters) : type;
            }

            var arguments = new Type[description.Arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                arguments[i] = ResolveSerializationType(description.Arguments[i], parameters);
            }

            return type.MakeGenericType(arguments);
        }

        private IBaseCodec? CreateBaseCodecInstance(Type fieldType, Type searchType)
        {
            object[]? constructorArguments = null;
            if (_baseCodecs.TryGetValue(searchType, out var codecType))
            {
                if (codecType.IsGenericTypeDefinition)
                {
                    codecType = ConstructGenericImplementation(codecType, fieldType.GetGenericArguments());
                }
            }
            else if (TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments) && typeof(IBaseCodec).IsAssignableFrom(surrogateCodecType))
            {
                codecType = surrogateCodecType;
            }

            return codecType != null ? (IBaseCodec)GetServiceOrCreateInstance(codecType, constructorArguments) : null;
        }

        private IDeepCopier? CreateCopierInstance(Type fieldType, Type searchType)
        {
            if (searchType == ObjectType)
                return _objectCopier;

            object[]? constructorArguments = null;
            if (_copiers.TryGetValue(searchType, out var copierType))
            {
                if (copierType.IsGenericTypeDefinition)
                {
                    copierType = ConstructGenericImplementation(copierType, fieldType.GetGenericArguments());
                }
            }
            else if (ShallowCopyableTypes.Contains(fieldType))
            {
                return ShallowCopier.Instance;
            }
            else if (fieldType.IsArray)
            {
                // Depending on the type of the array, select the base array copier or the multi-dimensional copier.
                var arrayCopierType = fieldType.IsSZArray ? typeof(ArrayCopier<>) : typeof(MultiDimensionalArrayCopier<>);
                copierType = ConstructGenericImplementation(arrayCopierType, fieldType.GetElementType()!);
            }
            else if (TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments))
            {
                copierType = surrogateCodecType;
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

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage("AOT", "IL3050",
            Justification = "Generated closed factories take priority. Metadata-based resolution can materialize a registered implementation whose closed native code is already rooted; unsupported native instantiations fail with registration guidance.")]
        [UnconditionalSuppressMessage("Trimming", "IL2055",
            Justification = "Registered implementation definitions preserve constructors and interfaces through annotated manifest APIs. Closed factories or typed generated dependencies preserve native instantiations; arbitrary unrooted shapes require an explicit registration.")]
#endif
        private static Type ConstructGenericImplementation(Type implementation, params Type[] arguments)
        {
            try
            {
                return implementation.MakeGenericType(arguments);
            }
            catch (NotSupportedException exception) when (!RuntimeFeature.IsDynamicCodeSupported)
            {
                throw new NotSupportedException(
                    $"The runtime cannot materialize serialization implementation {implementation} for [{string.Join(", ", arguments.Select(static type => type.ToString()))}]. Register its closed codec/copier and dependencies using a serializer context or closed factories.",
                    exception);
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
