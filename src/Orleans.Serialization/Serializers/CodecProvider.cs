using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
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
        private static readonly Type OpenGenericCodecType = typeof(IFieldCodec<>);
        private static readonly MethodInfo TypedCodecWrapperCreateMethod = typeof(CodecAdapter).GetMethod(nameof(CodecAdapter.CreateUntypedFromTyped), BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo TypedBaseCodecWrapperCreateMethod = typeof(CodecAdapter).GetMethod(nameof(BaseCodecAdapter.CreateUntypedFromTyped), BindingFlags.Public | BindingFlags.Static);

        private readonly object _initializationLock = new();

        private readonly ConcurrentDictionary<(Type, Type), IFieldCodec> _adaptedCodecs = new();
        private readonly ConcurrentDictionary<(Type, Type), IBaseCodec> _adaptedBaseCodecs = new();
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
        private readonly Dictionary<Type, Type> _baseCopiers = new();
        private readonly Dictionary<Type, Type> _activators = new();
        private readonly List<IGeneralizedCodec> _generalizedCodecs = new();
        private readonly List<ISpecializableCodec> _specializableCodecs = new();
        private readonly List<IGeneralizedBaseCodec> _generalizedBaseCodecs = new();
        private readonly List<ISpecializableBaseCodec> _specializableBaseCodecs = new();
        private readonly List<IGeneralizedCopier> _generalizedCopiers = new();
        private readonly List<ISpecializableCopier> _specializableCopiers = new();
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

            // Hard-code the object codec because many codecs implement IFieldCodec<object> and this is cleaner
            // than adding some filtering logic to find "the object codec" below.
            _fieldCodecs[typeof(object)] = typeof(ObjectCodec);

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
            AddFromMetadata(_baseCodecs, metadata.Serializers, typeof(IBaseCodec<>));
            AddFromMetadata(_valueSerializers, metadata.Serializers, typeof(IValueSerializer<>));
            AddFromMetadata(_fieldCodecs, metadata.Serializers, typeof(IFieldCodec<>));
            AddFromMetadata(_fieldCodecs, metadata.FieldCodecs, typeof(IFieldCodec<>));
            AddFromMetadata(_activators, metadata.Activators, typeof(IActivator<>));
            AddFromMetadata(_copiers, metadata.Copiers, typeof(IDeepCopier<>));
            AddFromMetadata(_converters, metadata.Converters, typeof(IConverter<,>));
            AddFromMetadata(_baseCopiers, metadata.Copiers, typeof(IBaseCopier<>));

            static void AddFromMetadata(Dictionary<Type, Type> resultCollection, HashSet<Type> metadataCollection, Type genericType)
            {
                Debug.Assert(genericType.GetGenericArguments().Length >= 1);

                foreach (var type in metadataCollection)
                {
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

                        if (genericArgument.IsConstructedGenericType && genericArgument.GenericTypeArguments.Any(arg => arg.IsGenericParameter))
                        {
                            genericArgument = genericArgument.GetGenericTypeDefinition();
                        }

                        resultCollection[genericArgument] = type;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public IFieldCodec<TField> TryGetCodec<TField>()
        {
            return _adaptedCodecs.TryGetValue((typeof(TField), typeof(TField)), out var existing)
                ? (IFieldCodec<TField>)existing : TryCreateCodec<TField>(typeof(TField));
        }

        /// <inheritdoc/>
        public IFieldCodec<object> GetCodec(Type fieldType)
        {
            var res = TryGetCodec(fieldType);
            if (res is null) ThrowCodecNotFound(fieldType);
            return res;
        }

        /// <inheritdoc/>
        public IFieldCodec<object> TryGetCodec(Type fieldType)
        {
            // If the field type is unavailable, return the void codec which can at least handle references.
            if (fieldType is null)
                return _voidCodec;

            return _adaptedCodecs.TryGetValue((fieldType, typeof(object)), out var existing)
                ? (IFieldCodec<object>)existing : TryCreateCodec<object>(fieldType);
        }

        /// <inheritdoc/>
        public IFieldCodec<TField> GetCodec<TField>()
        {
            var res = TryGetCodec<TField>();
            if (res is null) ThrowCodecNotFound(typeof(TField));
            return res;
        }

        private IFieldCodec<TField> TryCreateCodec<TField>(Type fieldType)
        {
            if (!_initialized) Initialize();

            // Try to find the codec from the configured codecs.
            IFieldCodec untypedResult;

            ThrowIfUnsupportedType(fieldType);

            if (fieldType.IsConstructedGenericType)
            {
                untypedResult = CreateCodecInstance(fieldType, fieldType.GetGenericTypeDefinition());
            }
            else
            {
                untypedResult = CreateCodecInstance(fieldType, fieldType);
            }

            if (untypedResult is null)
            {
                foreach (var specializableCodec in _specializableCodecs)
                {
                    if (specializableCodec.IsSupportedType(fieldType))
                    {
                        untypedResult = specializableCodec.GetSpecializedCodec(fieldType);
                    }
                }
            }

            if (untypedResult is null)
            {
                foreach (var dynamicCodec in _generalizedCodecs)
                {
                    if (dynamicCodec.IsSupportedType(fieldType))
                    {
                        untypedResult = dynamicCodec;
                        break;
                    }
                }
            }

            if (untypedResult is null && (fieldType.IsInterface || fieldType.IsAbstract))
            {
                untypedResult = (IFieldCodec)GetServiceOrCreateInstance(typeof(AbstractTypeSerializer<>).MakeGenericType(fieldType));
            }


            // Attempt to adapt the codec if it's not already adapted.
            IFieldCodec<TField> typedResult;
            switch (untypedResult)
            {
                case null:
                    return null;
                case IFieldCodec<TField> typedCodec:
                    typedResult = typedCodec;
                    break;
                case IWrappedCodec wrapped when wrapped.Inner is IFieldCodec<TField> typedCodec:
                    typedResult = typedCodec;
                    break;
                case IFieldCodec<object> objectCodec:
                    typedResult = CodecAdapter.CreateTypedFromUntyped<TField>(objectCodec);
                    break;
                default:
                    typedResult = WrapCodec(untypedResult);
                    break;
            }

            return (IFieldCodec<TField>)_adaptedCodecs.GetOrAdd((fieldType, typeof(TField)), typedResult);

            static IFieldCodec<TField> WrapCodec(IFieldCodec rawCodec)
            {
                var codecType = rawCodec.GetType();
                if (typeof(TField) == ObjectType)
                {
                    foreach (var @interface in codecType.GetInterfaces())
                    {
                        if (@interface.IsConstructedGenericType
                            && OpenGenericCodecType.IsAssignableFrom(@interface.GetGenericTypeDefinition()))
                        {
                            // Convert the typed codec provider into a wrapped object codec provider.
                            return TypedCodecWrapperCreateMethod.MakeGenericMethod(@interface.GetGenericArguments()).Invoke(null, new[] { rawCodec }) as IFieldCodec<TField>;
                        }
                    }
                }

                throw new InvalidOperationException($"Cannot convert codec of type {rawCodec.GetType()} to codec of type IFieldCodec<{typeof(TField)}>.");
            }
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

        private IBaseCodec<TField> TryGetBaseCodecInner<TField>(Type fieldType) where TField : class
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(fieldType);

            var resultFieldType = typeof(TField);
            var wasCreated = false;

            // Try to find the codec from the configured codecs.
            IBaseCodec untypedResult;

            if (!_adaptedBaseCodecs.TryGetValue((fieldType, resultFieldType), out untypedResult))
            {
                ThrowIfUnsupportedType(fieldType);

                if (fieldType.IsConstructedGenericType)
                {
                    untypedResult = CreateBaseCodecInstance(fieldType, fieldType.GetGenericTypeDefinition());
                }
                else
                {
                    untypedResult = CreateBaseCodecInstance(fieldType, fieldType);
                }

                if (untypedResult is null)
                {
                    foreach (var specializableCodec in _specializableBaseCodecs)
                    {
                        if (specializableCodec.IsSupportedType(fieldType))
                        {
                            untypedResult = specializableCodec.GetSpecializedCodec(fieldType);
                        }
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
                }

                wasCreated = untypedResult != null;
            }

            // Attempt to adapt the codec if it's not already adapted.
            IBaseCodec<TField> typedResult;
            var wasAdapted = false;
            switch (untypedResult)
            {
                case null:
                    return null;
                case IBaseCodec<TField> typedCodec:
                    typedResult = typedCodec;
                    break;
                case IWrappedCodec wrapped when wrapped.Inner is IBaseCodec<TField> typedCodec:
                    typedResult = typedCodec;
                    wasAdapted = true;
                    break;
                case IBaseCodec<object> objectCodec:
                    typedResult = BaseCodecAdapter.CreateTypedFromUntyped<TField>(objectCodec);
                    wasAdapted = true;
                    break;
                default:
                    typedResult = TryWrapCodec(untypedResult);
                    wasAdapted = true;
                    break;
            }

            // Store the results or throw if adaptation failed.
            if (typedResult != null && (wasCreated || wasAdapted))
            {
                untypedResult = typedResult;
                var key = (fieldType, resultFieldType);
                if (_adaptedBaseCodecs.TryGetValue(key, out var existing))
                {
                    typedResult = (IBaseCodec<TField>)existing;
                }
                else if (!_adaptedBaseCodecs.TryAdd(key, untypedResult))
                {
                    typedResult = (IBaseCodec<TField>)_adaptedBaseCodecs[key];
                }
            }
            else if (typedResult is null)
            {
                ThrowCannotConvert(untypedResult);
            }

            return typedResult;

            static IBaseCodec<TField> TryWrapCodec(object rawCodec)
            {
                var codecType = rawCodec.GetType();
                if (typeof(TField) == ObjectType)
                {
                    foreach (var @interface in codecType.GetInterfaces())
                    {
                        if (@interface.IsConstructedGenericType
                            && OpenGenericCodecType.IsAssignableFrom(@interface.GetGenericTypeDefinition()))
                        {
                            // Convert the typed codec provider into a wrapped object codec provider.
                            return TypedBaseCodecWrapperCreateMethod.MakeGenericMethod(@interface.GetGenericArguments()).Invoke(null, new[] { rawCodec }) as IBaseCodec<TField>;
                        }
                    }
                }

                return null;
            }

            static void ThrowCannotConvert(object rawCodec) => throw new InvalidOperationException($"Cannot convert codec of type {rawCodec.GetType()} to codec of type {typeof(IBaseCodec<TField>)}.");
        }

        /// <inheritdoc/>
        public IBaseCodec<TField> GetBaseCodec<TField>() where TField : class
        {
            var type = typeof(TField);

            var result = TryGetBaseCodecInner<TField>(type);

            if (result is null)
            {
                ThrowBaseCodecNotFound(type);
            }

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
        public IDeepCopier<T> TryGetDeepCopier<T>()
        {
            if (_typedCopiers.TryGetValue(typeof(T), out var existing))
                return (IDeepCopier<T>)existing;

            if (TryGetDeepCopier(typeof(T)) is not { } untypedResult)
                return null;

            var typedResult = untypedResult switch
            {
                IDeepCopier<T> typed => typed,
                IOptionalDeepCopier optional when optional.IsShallowCopyable() => new ShallowCopier<T>(),
                _ => new UntypedCopierWrapper<T>(untypedResult)
            };

            return (IDeepCopier<T>)_typedCopiers.GetOrAdd(typeof(T), typedResult);
        }

        /// <inheritdoc/>
        public IDeepCopier GetDeepCopier(Type fieldType)
        {
            var res = TryGetDeepCopier(fieldType);
            if (res is null) ThrowCopierNotFound(fieldType);
            return res;
        }

        /// <inheritdoc/>
        public IDeepCopier TryGetDeepCopier(Type fieldType)
        {
            // If the field type is unavailable, return the void copier which can at least handle references.
            return fieldType is null ? _voidCopier
                : _untypedCopiers.TryGetValue(fieldType, out var existing) ? existing
                : TryCreateCopier(fieldType) is { } res ? _untypedCopiers.GetOrAdd(fieldType, res)
                : null;
        }

        private IDeepCopier TryCreateCopier(Type fieldType)
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

        private object GetValueSerializerInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            object[] constructorArguments = null;
            if (_valueSerializers.TryGetValue(searchType, out var serializerType))
            {
                if (serializerType.IsGenericTypeDefinition)
                {
                    serializerType = serializerType.MakeGenericType(concreteType.GetGenericArguments());
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

        private object GetBaseCopierInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            object[] constructorArguments = null;
            if (_baseCopiers.TryGetValue(searchType, out var copierType))
            {
               // Use the detected copier type. 
                if (copierType.IsGenericTypeDefinition)
                {
                    copierType = copierType.MakeGenericType(concreteType.GetGenericArguments());
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

        private object GetActivatorInner(Type concreteType, Type searchType)
        {
            if (!_initialized) Initialize();

            ThrowIfUnsupportedType(concreteType);

            if (!_activators.TryGetValue(searchType, out var activatorType))
            {
                activatorType = typeof(DefaultActivator<>).MakeGenericType(concreteType);
            }
            else if (activatorType.IsGenericTypeDefinition)
            {
                activatorType = activatorType.MakeGenericType(concreteType.GetGenericArguments());
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

        private object GetServiceOrCreateInstance(Type type, object[] constructorArguments = null)
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

        private IFieldCodec CreateCodecInstance(Type fieldType, Type searchType)
        {
            object[] constructorArguments = null;
            if (_fieldCodecs.TryGetValue(searchType, out var codecType))
            {
                if (codecType.IsGenericTypeDefinition)
                {
                    codecType = codecType.MakeGenericType(fieldType.GetGenericArguments());
                }
            }
            else if (_baseCodecs.TryGetValue(searchType, out var baseCodecType))
            {
                if (baseCodecType.IsGenericTypeDefinition)
                {
                    baseCodecType = baseCodecType.MakeGenericType(fieldType.GetGenericArguments());
                }

                // If there is a base type serializer for this type, create a codec which will then accept that base type serializer.
                codecType = typeof(ConcreteTypeSerializer<,>).MakeGenericType(fieldType, baseCodecType);
                constructorArguments = new[] { GetServiceOrCreateInstance(baseCodecType) };
            }
            else if (_valueSerializers.TryGetValue(searchType, out var valueSerializerType))
            {
                if (valueSerializerType.IsGenericTypeDefinition)
                {
                    valueSerializerType = valueSerializerType.MakeGenericType(fieldType.GetGenericArguments());
                }

                // If there is a value serializer for this type, create a codec which will then accept that value serializer.
                codecType = typeof(ValueSerializer<,>).MakeGenericType(fieldType, valueSerializerType);
                constructorArguments = new[] { GetServiceOrCreateInstance(valueSerializerType) };
            }
            else if (fieldType.IsArray)
            {
                // Depending on the type of the array, select the base array codec or the multi-dimensional codec.
                var arrayCodecType = fieldType.IsSZArray ? typeof(ArrayCodec<>) : typeof(MultiDimensionalArrayCodec<>);
                codecType = arrayCodecType.MakeGenericType(fieldType.GetElementType());
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
                    fieldType,
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

        private bool TryGetSurrogateCodec(Type fieldType, Type searchType, out Type surrogateCodecType, out object[] constructorArguments)
        {
            if (_converters.TryGetValue(searchType, out var converterType))
            {
                if (converterType.IsGenericTypeDefinition)
                {
                    converterType = converterType.MakeGenericType(fieldType.GetGenericArguments());
                }

                var converterInterfaceArgs = Array.Empty<Type>();
                foreach (var @interface in converterType.GetInterfaces())
                {
                    if (@interface.IsConstructedGenericType && @interface.GetGenericTypeDefinition() == typeof(IConverter<,>))
                    {
                        converterInterfaceArgs = @interface.GetGenericArguments();
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

        private IBaseCodec CreateBaseCodecInstance(Type fieldType, Type searchType)
        {
            object[] constructorArguments = null;
            if (_baseCodecs.TryGetValue(searchType, out var codecType))
            {
                if (codecType.IsGenericTypeDefinition)
                {
                    codecType = codecType.MakeGenericType(fieldType.GetGenericArguments());
                }
            }
            else if (TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments) && typeof(IBaseCodec).IsAssignableFrom(surrogateCodecType))
            {
                codecType = surrogateCodecType;
            }

            return codecType != null ? (IBaseCodec)GetServiceOrCreateInstance(codecType, constructorArguments) : null;
        }

        private IDeepCopier CreateCopierInstance(Type fieldType, Type searchType)
        {
            if (searchType == ObjectType)
                return _objectCopier;

            object[] constructorArguments = null;
            if (_copiers.TryGetValue(searchType, out var copierType))
            {
                if (copierType.IsGenericTypeDefinition)
                {
                    copierType = copierType.MakeGenericType(fieldType.GetGenericArguments());
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
                copierType = arrayCopierType.MakeGenericType(fieldType.GetElementType());
            }
            else if (TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments))
            {
                copierType = surrogateCodecType;
            }
            else if (searchType.BaseType is { } baseType && CreateCopierInstance(fieldType, baseType) is IDerivedTypeCopier baseCopier)
            {
                // Find copiers which generalize over all subtypes.
                return baseCopier;
            }

            return copierType != null ? (IDeepCopier)GetServiceOrCreateInstance(copierType, constructorArguments) : null;
        }

        private static void ThrowPointerType(Type fieldType) => throw new NotSupportedException($"Type {fieldType} is a pointer type and is therefore not supported.");

        private static void ThrowByRefType(Type fieldType) => throw new NotSupportedException($"Type {fieldType} is a by-ref type and is therefore not supported.");

        private static void ThrowGenericTypeDefinition(Type fieldType) => throw new InvalidOperationException($"Type {fieldType} is a non-constructed generic type and is therefore unsupported.");

        private static void ThrowCodecNotFound(Type fieldType) => throw new CodecNotFoundException($"Could not find a codec for type {fieldType}.");

        private static void ThrowCopierNotFound(Type type) => throw new CodecNotFoundException($"Could not find a copier for type {type}.");

        private static void ThrowBaseCodecNotFound(Type fieldType) => throw new KeyNotFoundException($"Could not find a base type serializer for type {fieldType}.");

        private static void ThrowValueSerializerNotFound(Type fieldType) => throw new KeyNotFoundException($"Could not find a value serializer for type {fieldType}.");

        private static void ThrowActivatorNotFound(Type type) => throw new KeyNotFoundException($"Could not find an activator for type {type}.");

        private static void ThrowBaseCopierNotFound(Type type) => throw new KeyNotFoundException($"Could not find a base type copier for type {type}.");
    }
}
