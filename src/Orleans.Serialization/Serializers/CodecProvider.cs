using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        private readonly Dictionary<Type, List<(Type Implementation, SerializationContract Contract)>> _converterContracts = new();
        private readonly Dictionary<(Type Contract, Type Target, Type Implementation), List<SerializationType>> _targetDescriptions = new();
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
        private bool _initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="CodecProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="codecConfiguration">The codec configuration.</param>
        public CodecProvider(IServiceProvider serviceProvider, IOptions<TypeManifestOptions> codecConfiguration)
        {
            _serviceProvider = serviceProvider;

            ConsumeMetadata(codecConfiguration);
        }

        /// <inheritdoc/>
        public IServiceProvider Services => _serviceProvider;

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
                                ? description.Type is { IsGenericTypeDefinition: true } definition
                                    ? definition
                                    : ResolveSerializationType(description, type.GetGenericArguments())
                                : registration.TargetType!;
                            if (target != typeof(object))
                            {
                                resultCollection[target] = type;
                                if (registration.TargetDescription is { } targetDescription)
                                {
                                    var key = (genericType, target, type);
                                    if (!_targetDescriptions.TryGetValue(key, out var descriptions))
                                    {
                                        _targetDescriptions[key] = descriptions = new();
                                    }

                                    descriptions.Add(targetDescription);
                                }
                                if (genericType == typeof(IConverter<,>))
                                {
                                    if (!_converterContracts.TryGetValue(target, out var converterRegistrations))
                                    {
                                        _converterContracts[target] = converterRegistrations = new();
                                    }

                                    converterRegistrations.Add((type, registration));
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
                                ? description.Type is { IsGenericTypeDefinition: true } definition
                                    ? definition
                                    : ResolveSerializationType(description, type.GetGenericArguments())
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
            if (_typedCodecs.TryGetValue(fieldType, out var existing))
                return (IFieldCodec<TField>)existing;

            if (TryGetCodec(fieldType) is not { } untypedResult)
                return null;

            var typedResult = untypedResult switch
            {
                IFieldCodec<TField> typed => typed,
                _ when untypedResult.GetType() == typeof(AbstractTypeSerializer) => new AbstractTypeSerializerWrapper<TField>(),
                _ => new UntypedCodecWrapper<TField>(untypedResult)
            };

            return (IFieldCodec<TField>)_typedCodecs.GetOrAdd(fieldType, typedResult);
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
            // If the field type is unavailable, return the void codec which can at least handle references.
            return fieldType is null ? _voidCodec
                : _untypedCodecs.TryGetValue(fieldType, out var existing) ? existing
                : TryCreateCodec(fieldType) is { } res ? _untypedCodecs.GetOrAdd(fieldType, res) : null;
        }

        private IFieldCodec? TryCreateCodec(Type fieldType)
        {
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
            var type = typeof(T);
            var searchType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

            var res = GetActivatorInner(type, searchType);
            if (res is null) ThrowActivatorNotFound(type);
            return (IActivator<T>)res;
        }

        private IBaseCodec<TField>? TryCreateBaseCodec<TField>(Type fieldType) where TField : class
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

            return (IBaseCodec<TField>)_typedBaseCodecs.GetOrAdd(fieldType, typedResult);

            static void ThrowCannotConvert(object rawCodec) => throw new InvalidOperationException($"Cannot convert codec of type {rawCodec.GetType()} to codec of type {typeof(IBaseCodec<TField>)}.");
        }

        /// <inheritdoc/>
        public IBaseCodec<TField> GetBaseCodec<TField>() where TField : class
        {
            var type = typeof(TField);
            if (_typedBaseCodecs.TryGetValue(type, out var existing))
                return (IBaseCodec<TField>)existing;

            var result = TryCreateBaseCodec<TField>(type);
            if (result is null) ThrowBaseCodecNotFound(type);
            return result;
        }

        /// <inheritdoc/>
        public IValueSerializer<TField> GetValueSerializer<TField>() where TField : struct
        {
            var type = typeof(TField);
            var searchType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

            var res = GetValueSerializerInner(type, searchType);
            if (res is null) ThrowValueSerializerNotFound(type);
            return (IValueSerializer<TField>)res;
        }

        /// <inheritdoc/>
        public IBaseCopier<TField> GetBaseCopier<TField>() where TField : class
        {
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
            if (_typedCopiers.TryGetValue(type, out var existing))
                return (IDeepCopier<T>)existing;

            if (TryGetDeepCopier(type) is not { } untypedResult)
                return null;

            var typedResult = untypedResult switch
            {
                IDeepCopier<T> typed => typed,
                IOptionalDeepCopier optional when optional.IsShallowCopyable() => new ShallowCopier<T>(),
                _ => new UntypedCopierWrapper<T>(untypedResult)
            };

            return (IDeepCopier<T>)_typedCopiers.GetOrAdd(type, typedResult);
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
            // If the field type is unavailable, return the void copier which can at least handle references.
            return fieldType is null ? _voidCopier
                : _untypedCopiers.TryGetValue(fieldType, out var existing) ? existing
                : TryCreateCopier(fieldType) is { } res ? _untypedCopiers.GetOrAdd(fieldType, res)
                : null;
        }

        private IDeepCopier? TryCreateCopier(Type fieldType)
        {
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
                    serializerType = CloseImplementation(serializerType, concreteType, typeof(IValueSerializer<>));
                    if (serializerType is null)
                    {
                        return null;
                    }
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

            if (!_instantiatedValueSerializers.TryGetValue(serializerType, out var result))
            {
                result = _instantiatedValueSerializers.GetOrAdd(serializerType, GetServiceOrCreateInstance(serializerType, constructorArguments));
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
                    copierType = CloseImplementation(copierType, concreteType, typeof(IBaseCopier<>));
                    if (copierType is null)
                    {
                        return null;
                    }
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

            if (!_instantiatedBaseCopiers.TryGetValue(copierType, out var result))
            {
                result = _instantiatedBaseCopiers.GetOrAdd(copierType, GetServiceOrCreateInstance(copierType, constructorArguments));
            }

            return result;
        }

        private object? GetActivatorInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            if (!_activators.TryGetValue(searchType, out var activatorType))
            {
                if (searchType.IsValueType)
                {
                    activatorType = typeof(DefaultValueTypeActivator<>).MakeGenericType(concreteType);
                }
                else
                {
                    activatorType = typeof(DefaultReferenceTypeActivator<>).MakeGenericType(concreteType);
                }
            }
            else if (activatorType.IsGenericTypeDefinition)
            {
                activatorType = CloseImplementation(activatorType, concreteType, typeof(IActivator<>));
                if (activatorType is null)
                {
                    return null;
                }
            }

            if (!_instantiatedActivators.TryGetValue(activatorType, out var result))
            {
                result = _instantiatedActivators.GetOrAdd(activatorType, GetServiceOrCreateInstance(activatorType));
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
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(MultiDimensionalArrayCodec<>))]
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
            var result = OrleansGeneratedCodeHelper.TryGetService(type);
            if (result != null)
            {
                return result;
            }

            result = _serviceProvider.GetService(type);
            if (result != null)
            {
                return result;
            }

            result = ActivatorUtilities.CreateInstance(_serviceProvider, type, constructorArguments ?? Array.Empty<object>());
            return result;
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
                    codecType = CloseImplementation(codecType, fieldType, typeof(IFieldCodec<>));
                    if (codecType is null)
                    {
                        return null;
                    }
                }
            }
            else if (_baseCodecs.TryGetValue(searchType, out var baseCodecType))
            {
                if (baseCodecType.IsGenericTypeDefinition)
                {
                    baseCodecType = CloseImplementation(baseCodecType, fieldType, typeof(IBaseCodec<>));
                    if (baseCodecType is null)
                    {
                        return null;
                    }
                }

                // If there is a base type serializer for this type, create a codec which will then accept that base type serializer.
                codecType = typeof(ConcreteTypeSerializer<,>).MakeGenericType(fieldType, baseCodecType);
                constructorArguments = new[] { GetServiceOrCreateInstance(baseCodecType) };
            }
            else if (_valueSerializers.TryGetValue(searchType, out var valueSerializerType))
            {
                if (valueSerializerType.IsGenericTypeDefinition)
                {
                    valueSerializerType = CloseImplementation(valueSerializerType, fieldType, typeof(IValueSerializer<>));
                    if (valueSerializerType is null)
                    {
                        return null;
                    }
                }

                // If there is a value serializer for this type, create a codec which will then accept that value serializer.
                codecType = typeof(ValueSerializer<,>).MakeGenericType(fieldType, valueSerializerType);
                constructorArguments = new[] { GetServiceOrCreateInstance(valueSerializerType) };
            }
            else if (fieldType.IsArray)
            {
                // Depending on the type of the array, select the base array codec or the multi-dimensional codec.
                var arrayCodecType = fieldType.IsSZArray ? typeof(ArrayCodec<>) : typeof(MultiDimensionalArrayCodec<>);
                codecType = arrayCodecType.MakeGenericType(fieldType.GetElementType()!);
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

        private bool TryGetSurrogateCodec(Type fieldType, Type searchType, [NotNullWhen(true)] out Type? surrogateCodecType, [NotNullWhen(true)] out object[]? constructorArguments)
        {
            if (_converters.TryGetValue(searchType, out var converterDefinition))
            {
                Type converterType;
                var converterInterfaceArgs = Array.Empty<Type>();
                if (TryGetConverterContract(searchType, converterDefinition, fieldType, out var registration))
                {
                    converterType = converterDefinition;
                    if (converterType.IsGenericTypeDefinition)
                    {
                        var closed = CloseImplementation(converterType, fieldType, typeof(IConverter<,>));
                        if (closed is null)
                        {
                            surrogateCodecType = null;
                            constructorArguments = null;
                            return false;
                        }

                        converterType = closed;
                    }

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
                    if (_converterContracts.ContainsKey(searchType))
                    {
                        surrogateCodecType = null;
                        constructorArguments = null;
                        return false;
                    }

                    converterType = converterDefinition.IsGenericTypeDefinition
                        ? converterDefinition.MakeGenericType(fieldType.GetGenericArguments())
                        : converterDefinition;
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
                    surrogateCodecType = typeof(ValueTypeSurrogateCodec<,,>).MakeGenericType(typeArgs);
                }
                else
                {
                    surrogateCodecType = typeof(SurrogateCodec<,,>).MakeGenericType(typeArgs);
                }

                return true;
            }

            surrogateCodecType = null;
            constructorArguments = null;
            return false;
        }

        private Type? CloseImplementation(Type implementation, Type targetType, Type contractType)
        {
            var searchType = targetType.IsConstructedGenericType ? targetType.GetGenericTypeDefinition() : targetType;
            if (!_targetDescriptions.TryGetValue((contractType, searchType, implementation), out var descriptions))
            {
                return implementation.MakeGenericType(targetType.GetGenericArguments());
            }

            var count = implementation.GetGenericArguments().Length;
            for (var candidate = descriptions.Count - 1; candidate >= 0; candidate--)
            {
                var arguments = new Type?[count];
                if (!BindTypeArguments(descriptions[candidate], targetType, arguments))
                {
                    continue;
                }

                var resolved = new Type[arguments.Length];
                for (var i = 0; i < resolved.Length; i++)
                {
                    resolved[i] = arguments[i] ?? throw new InvalidOperationException(
                        $"Serialization contract for {implementation} does not bind generic parameter {i} from target {targetType}.");
                }

                return implementation.MakeGenericType(resolved);
            }

            return null;
        }

        private bool TryGetConverterContract(Type searchType, Type implementation, Type target, out SerializationContract contract)
        {
            if (_converterContracts.TryGetValue(searchType, out var registrations))
            {
                for (var i = registrations.Count - 1; i >= 0; i--)
                {
                    var candidate = registrations[i];
                    if (candidate.Implementation != implementation)
                    {
                        continue;
                    }

                    if (candidate.Contract.TargetDescription is { } description
                        && !BindTypeArguments(description, target, new Type?[implementation.GetGenericArguments().Length]))
                    {
                        continue;
                    }

                    contract = candidate.Contract;
                    return true;
                }
            }

            contract = default;
            return false;
        }

        private static bool BindTypeArguments(SerializationType description, Type target, Type?[] arguments)
        {
            if (description.ParameterIndex >= 0)
            {
                var index = description.ParameterIndex;
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

            if (description.ArrayRank > 0)
            {
                return target.IsArray && target.GetArrayRank() == description.ArrayRank
                    && (description.ArrayRank != 1 || target.IsSZArray)
                    && BindTypeArguments(description.Arguments[0], target.GetElementType()!, arguments);
            }

            if (description.Arguments.Length == 0)
            {
                return description.Type == target;
            }

            if (!target.IsConstructedGenericType || target.GetGenericTypeDefinition() != description.Type)
            {
                return false;
            }

            var parameters = target.GetGenericArguments();
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!BindTypeArguments(description.Arguments[i], parameters[i], arguments))
                {
                    return false;
                }
            }

            return true;
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
                    codecType = CloseImplementation(codecType, fieldType, typeof(IBaseCodec<>));
                    if (codecType is null)
                    {
                        return null;
                    }
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
                    copierType = CloseImplementation(copierType, fieldType, typeof(IDeepCopier<>));
                    if (copierType is null)
                    {
                        return null;
                    }
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
                copierType = arrayCopierType.MakeGenericType(fieldType.GetElementType()!);
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

        [DoesNotReturn]
        private static void ThrowPointerType(Type fieldType) => throw new NotSupportedException($"Type {fieldType} is a pointer type and is therefore not supported.");

        [DoesNotReturn]
        private static void ThrowByRefType(Type fieldType) => throw new NotSupportedException($"Type {fieldType} is a by-ref type and is therefore not supported.");

        [DoesNotReturn]
        private static void ThrowGenericTypeDefinition(Type fieldType) => throw new InvalidOperationException($"Type {fieldType} is a non-constructed generic type and is therefore unsupported.");

        [DoesNotReturn]
        private static void ThrowCodecNotFound(Type fieldType) => throw new CodecNotFoundException($"Could not find a codec for type {fieldType}.");

        [DoesNotReturn]
        private static void ThrowCopierNotFound(Type type) => throw new CodecNotFoundException($"Could not find a copier for type {type}.");

        [DoesNotReturn]
        private static void ThrowBaseCodecNotFound(Type fieldType) => throw new KeyNotFoundException($"Could not find a base type serializer for type {fieldType}.");

        [DoesNotReturn]
        private static void ThrowValueSerializerNotFound(Type fieldType) => throw new KeyNotFoundException($"Could not find a value serializer for type {fieldType}.");

        [DoesNotReturn]
        private static void ThrowActivatorNotFound(Type type) => throw new KeyNotFoundException($"Could not find an activator for type {type}.");

        [DoesNotReturn]
        private static void ThrowBaseCopierNotFound(Type type) => throw new KeyNotFoundException($"Could not find a base type copier for type {type}.");
    }
}
