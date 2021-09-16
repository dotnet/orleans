using Orleans.Serialization.Activators;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

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

        private static readonly Type OpenGenericCopierType = typeof(IDeepCopier<>);
        private static readonly MethodInfo TypedCopierWrapperCreateMethod = typeof(CopierAdapter).GetMethod(nameof(CopierAdapter.CreateUntypedFromTyped), BindingFlags.Public | BindingFlags.Static);

        private readonly object _initializationLock = new();

        private readonly ConcurrentDictionary<(Type, Type), IFieldCodec> _adaptedCodecs = new(TypeTypeValueTupleComparer.Instance);
        private readonly ConcurrentDictionary<(Type, Type), IBaseCodec> _adaptedBaseCodecs = new(TypeTypeValueTupleComparer.Instance);
        private readonly ConcurrentDictionary<(Type, Type), IDeepCopier> _adaptedCopiers = new(TypeTypeValueTupleComparer.Instance);

        private readonly ConcurrentDictionary<Type, object> _instantiatedBaseCodecs = new();
        private readonly ConcurrentDictionary<Type, object> _instantiatedBaseCopiers = new();
        private readonly ConcurrentDictionary<Type, object> _instantiatedValueSerializers = new();
        private readonly ConcurrentDictionary<Type, object> _instantiatedActivators = new();
        private readonly ConcurrentDictionary<Type, object> _instantiatedConverters = new();
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
        private readonly ObjectCopier _objectCopier;
        private readonly IServiceProvider _serviceProvider;
        private readonly IDeepCopier _voidCopier = new VoidCopier();
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
            _copiers[typeof(object)] = typeof(ObjectCopier);
            _objectCopier = new ObjectCopier();

            ConsumeMetadata(codecConfiguration);
        }

        /// <inheritdoc/>
        public IServiceProvider Services => _serviceProvider;

        private void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            lock (_initializationLock)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;

                _generalizedCodecs.AddRange(_serviceProvider.GetServices<IGeneralizedCodec>());
                _generalizedBaseCodecs.AddRange(_serviceProvider.GetServices<IGeneralizedBaseCodec>());
                _generalizedCopiers.AddRange(_serviceProvider.GetServices<IGeneralizedCopier>());

                _specializableCodecs.AddRange(_serviceProvider.GetServices<ISpecializableCodec>());
                _specializableCopiers.AddRange(_serviceProvider.GetServices<ISpecializableCopier>());
                _specializableBaseCodecs.AddRange(_serviceProvider.GetServices<ISpecializableBaseCodec>());
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

            static void AddFromMetadata(IDictionary<Type, Type> resultCollection, IEnumerable<Type> metadataCollection, Type genericType)
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
        public IFieldCodec<TField> TryGetCodec<TField>() => TryGetCodecInner<TField>(typeof(TField));

        /// <inheritdoc/>
        public IFieldCodec<object> GetCodec(Type fieldType) => TryGetCodec(fieldType) ?? ThrowCodecNotFound<object>(fieldType);

        /// <inheritdoc/>
        public IFieldCodec<object> TryGetCodec(Type fieldType) => TryGetCodecInner<object>(fieldType);

        /// <inheritdoc/>
        public IFieldCodec<TField> GetCodec<TField>() => TryGetCodec<TField>() ?? ThrowCodecNotFound<TField>(typeof(TField));

        private IFieldCodec<TField> TryGetCodecInner<TField>(Type fieldType)
        {
            if (!_initialized)
            {
                Initialize();
            }

            var resultFieldType = typeof(TField);
            var wasCreated = false;

            // Try to find the codec from the configured codecs.
            IFieldCodec untypedResult;

            // If the field type is unavailable, return the void codec which can at least handle references.
            if (fieldType is null)
            {
                untypedResult = _voidCodec;
            }
            else if (!_adaptedCodecs.TryGetValue((fieldType, resultFieldType), out untypedResult))
            {
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

                wasCreated = untypedResult != null;
            }

            // Attempt to adapt the codec if it's not already adapted.
            IFieldCodec<TField> typedResult;
            var wasAdapted = false;
            switch (untypedResult)
            {
                case null:
                    return null;
                case IFieldCodec<TField> typedCodec:
                    typedResult = typedCodec;
                    break;
                case IWrappedCodec wrapped when wrapped.Inner is IFieldCodec<TField> typedCodec:
                    typedResult = typedCodec;
                    wasAdapted = true;
                    break;
                case IFieldCodec<object> objectCodec:
                    typedResult = CodecAdapter.CreateTypedFromUntyped<TField>(objectCodec);
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
                if (_adaptedCodecs.TryGetValue(key, out var existing))
                {
                    typedResult = (IFieldCodec<TField>)existing;
                }
                else if (!_adaptedCodecs.TryAdd(key, untypedResult))
                {
                    typedResult = (IFieldCodec<TField>)_adaptedCodecs[key];
                }
            }
            else if (typedResult is null)
            {
                ThrowCannotConvert(untypedResult);
            }

            return typedResult;

            static IFieldCodec<TField> TryWrapCodec(object rawCodec)
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
                            return TypedCodecWrapperCreateMethod.MakeGenericMethod(@interface.GetGenericArguments()[0], codecType).Invoke(null, new[] { rawCodec }) as IFieldCodec<TField>;
                        }
                    }
                }

                return null;
            }

            static void ThrowCannotConvert(object rawCodec)
            {
                throw new InvalidOperationException($"Cannot convert codec of type {rawCodec.GetType()} to codec of type {typeof(IFieldCodec<TField>)}.");
            }
        }

        /// <inheritdoc/>
        public IActivator<T> GetActivator<T>()
        {
            if (!_initialized)
            {
                Initialize();
            }

            ThrowIfUnsupportedType(typeof(T));
            var type = typeof(T);
            var searchType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

            return GetActivatorInner<T>(type, searchType) ?? ThrowActivatorNotFound<T>(type);
        }

        private IBaseCodec<TField> TryGetBaseCodecInner<TField>(Type fieldType) where TField : class
        {
            if (!_initialized)
            {
                Initialize();
            }

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
                            return TypedBaseCodecWrapperCreateMethod.MakeGenericMethod(@interface.GetGenericArguments()[0], codecType).Invoke(null, new[] { rawCodec }) as IBaseCodec<TField>;
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
            if (!_initialized)
            {
                Initialize();
            }

            ThrowIfUnsupportedType(typeof(TField));
            var type = typeof(TField);

            var result = TryGetBaseCodecInner<TField>(type);

            if (result is null)
            {
                ThrowBaseCodecNotFound<TField>(type);
            }

            return result;
        }

        /// <inheritdoc/>
        public IValueSerializer<TField> GetValueSerializer<TField>() where TField : struct
        {
            if (!_initialized)
            {
                Initialize();
            }

            ThrowIfUnsupportedType(typeof(TField));
            var type = typeof(TField);
            var searchType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

            return GetValueSerializerInner<TField>(type, searchType) ?? ThrowValueSerializerNotFound<TField>(type);
        }

        /// <inheritdoc/>
        public IBaseCopier<TField> GetBaseCopier<TField>() where TField : class
        {
            if (!_initialized)
            {
                Initialize();
            }

            ThrowIfUnsupportedType(typeof(TField));
            var type = typeof(TField);
            var searchType = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;

            return GetBaseCopierInner<TField>(type, searchType) ?? ThrowBaseCopierNotFound<TField>(type);
        }

        /// <inheritdoc/>
        public IDeepCopier<T> GetDeepCopier<T>() => TryGetCopierInner<T>(typeof(T)) ?? ThrowCopierNotFound<T>(typeof(T));

        /// <inheritdoc/>
        public IDeepCopier<T> TryGetDeepCopier<T>() => TryGetCopierInner<T>(typeof(T));

        /// <inheritdoc/>
        public IDeepCopier<object> GetDeepCopier(Type fieldType) => TryGetCopierInner<object>(fieldType) ?? ThrowCopierNotFound<object>(fieldType);

        /// <inheritdoc/>
        public IDeepCopier<object> TryGetDeepCopier(Type fieldType) => TryGetCopierInner<object>(fieldType);
        
        private IDeepCopier<T> TryGetCopierInner<T>(Type fieldType)
        {
            if (!_initialized)
            {
                Initialize();
            }

            var resultFieldType = typeof(T);
            var wasCreated = false;

            // Try to find the copier from the configured copiers.
            IDeepCopier untypedResult;

            // If the field type is unavailable, return the void copier which can at least handle references.
            if (fieldType is null)
            {
                untypedResult = _voidCopier;
            }
            else if (!_adaptedCopiers.TryGetValue((fieldType, resultFieldType), out untypedResult))
            {
                ThrowIfUnsupportedType(fieldType);

                if (fieldType.IsConstructedGenericType)
                {
                    untypedResult = CreateCopierInstance(fieldType, fieldType.GetGenericTypeDefinition());
                }
                else
                {
                    untypedResult = CreateCopierInstance(fieldType, fieldType);
                }

                if (untypedResult is null)
                {
                    foreach (var specializableCopier in _specializableCopiers)
                    {
                        if (specializableCopier.IsSupportedType(fieldType))
                        {
                            untypedResult = specializableCopier.GetSpecializedCopier(fieldType);
                            break;
                        }
                    }
                }

                if (untypedResult is null)
                {
                    foreach (var dynamicCopier in _generalizedCopiers)
                    {
                        if (dynamicCopier.IsSupportedType(fieldType))
                        {
                            untypedResult = dynamicCopier;
                            break;
                        }
                    }
                }

                if (untypedResult is null && (fieldType.IsInterface || fieldType.IsAbstract))
                {
                    untypedResult = _objectCopier;
                }

                wasCreated = untypedResult != null;
            }

            // Attempt to adapt the copier if it's not already adapted.
            IDeepCopier<T> typedResult;
            var wasAdapted = false;
            switch (untypedResult)
            {
                case null:
                    return null;
                case IDeepCopier<T> typedCopier:
                    typedResult = typedCopier;
                    break;
                case IWrappedCodec wrapped when wrapped.Inner is IDeepCopier<T> typedCopier:
                    typedResult = typedCopier;
                    wasAdapted = true;
                    break;
                case IDeepCopier<object> objectCopier:
                    typedResult = CopierAdapter.CreateTypedFromUntyped<T>(objectCopier);
                    wasAdapted = true;
                    break;
                default:
                    typedResult = TryWrapCopier(untypedResult);
                    wasAdapted = true;
                    break;
            }

            // Store the results or throw if adaptation failed.
            if (typedResult is not null && (wasCreated || wasAdapted))
            {
                untypedResult = typedResult;
                var key = (fieldType, resultFieldType);
                if (_adaptedCopiers.TryGetValue(key, out var existing))
                {
                    typedResult = (IDeepCopier<T>)existing;
                }
                else if (!_adaptedCopiers.TryAdd(key, untypedResult))
                {
                    typedResult = (IDeepCopier<T>)_adaptedCopiers[key];
                }
            }
            else if (typedResult is null)
            {
                ThrowCannotConvert(untypedResult);
            }

            return typedResult;

            static IDeepCopier<T> TryWrapCopier(object rawCopier)
            {
                var copierType = rawCopier.GetType();
                if (typeof(T) == ObjectType)
                {
                    foreach (var @interface in copierType.GetInterfaces())
                    {
                        if (@interface.IsConstructedGenericType
                            && OpenGenericCopierType.IsAssignableFrom(@interface.GetGenericTypeDefinition()))
                        {
                            // Convert the typed copier provider into a wrapped object copier provider.
                            return TypedCopierWrapperCreateMethod.MakeGenericMethod(@interface.GetGenericArguments()[0], copierType).Invoke(null, new[] { rawCopier }) as IDeepCopier<T>;
                        }
                    }
                }

                return null;
            }

            static void ThrowCannotConvert(object rawCopier)
            {
                throw new InvalidOperationException($"Cannot convert copier of type {rawCopier.GetType()} to copier of type {typeof(IDeepCopier<T>)}.");
            }
        }

        private IValueSerializer<TField> GetValueSerializerInner<TField>(Type concreteType, Type searchType) where TField : struct
        {
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

            if (serializerType is null)
            {
                return null;
            }

            if (!_instantiatedValueSerializers.TryGetValue(serializerType, out var result))
            {
                result = GetServiceOrCreateInstance(serializerType, constructorArguments);
                _ = _instantiatedValueSerializers.TryAdd(serializerType, result);
            }

            return (IValueSerializer<TField>)result;
        }

        private IBaseCopier<T> GetBaseCopierInner<T>(Type concreteType, Type searchType) where T : class
        {
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

            if (copierType is null)
            {
                return null;
            }

            if (!_instantiatedBaseCopiers.TryGetValue(copierType, out var result))
            {
                result = GetServiceOrCreateInstance(copierType, constructorArguments);
                _ = _instantiatedBaseCopiers.TryAdd(copierType, result);
            }

            return (IBaseCopier<T>)result;
        }

        private IActivator<T> GetActivatorInner<T>(Type concreteType, Type searchType)
        {
            if (!_activators.TryGetValue(searchType, out var activatorType))
            {
                activatorType = typeof(DefaultActivator<>).MakeGenericType(concreteType);
            }

            if (activatorType.IsGenericTypeDefinition)
            {
                activatorType = activatorType.MakeGenericType(concreteType.GetGenericArguments());
            }

            if (!_instantiatedActivators.TryGetValue(activatorType, out var result))
            {
                result = GetServiceOrCreateInstance(activatorType);
                _ = _instantiatedActivators.TryAdd(activatorType, result);
            }

            return (IActivator<T>)result;
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
                // Depending on the rank of the array (1 or higher), select the base array codec or the multi-dimensional codec.
                var arrayCodecType = fieldType.GetArrayRank() == 1 ? typeof(ArrayCodec<>) : typeof(MultiDimensionalArrayCodec<>);
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
                copierType = typeof(ShallowCopyableTypeCopier<>).MakeGenericType(fieldType);
            }
            else if (fieldType.IsArray)
            {
                // Depending on the rank of the array (1 or higher), select the base array copier or the multi-dimensional copier.
                var arrayCopierType = fieldType.GetArrayRank() == 1 ? typeof(ArrayCopier<>) : typeof(MultiDimensionalArrayCopier<>);
                copierType = arrayCopierType.MakeGenericType(fieldType.GetElementType());
            }
            else if (TryGetSurrogateCodec(fieldType, searchType, out var surrogateCodecType, out constructorArguments))
            {
                copierType = surrogateCodecType;
            }
            else if (searchType.BaseType is object && CreateCopierInstance(fieldType, searchType.BaseType) is IDerivedTypeCopier baseCopier)
            {
                // Find copiers which generalize over all subtypes.
                return baseCopier;
            }

            return copierType != null ? (IDeepCopier)GetServiceOrCreateInstance(copierType, constructorArguments) : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowPointerType(Type fieldType) => throw new NotSupportedException($"Type {fieldType} is a pointer type and is therefore not supported.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowByRefType(Type fieldType) => throw new NotSupportedException($"Type {fieldType} is a by-ref type and is therefore not supported.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowGenericTypeDefinition(Type fieldType) => throw new InvalidOperationException($"Type {fieldType} is a non-constructed generic type and is therefore unsupported.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IFieldCodec<TField> ThrowCodecNotFound<TField>(Type fieldType) => throw new CodecNotFoundException($"Could not find a codec for type {fieldType}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IDeepCopier<T> ThrowCopierNotFound<T>(Type type) => throw new CodecNotFoundException($"Could not find a copier for type {type}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IBaseCodec<TField> ThrowBaseCodecNotFound<TField>(Type fieldType) where TField : class => throw new KeyNotFoundException($"Could not find a base type serializer for type {fieldType}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IValueSerializer<TField> ThrowValueSerializerNotFound<TField>(Type fieldType) where TField : struct => throw new KeyNotFoundException($"Could not find a value serializer for type {fieldType}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IActivator<T> ThrowActivatorNotFound<T>(Type type) => throw new KeyNotFoundException($"Could not find an activator for type {type}.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IBaseCopier<T> ThrowBaseCopierNotFound<T>(Type type) where T : class => throw new KeyNotFoundException($"Could not find a base type copier for type {type}.");

        private sealed class TypeTypeValueTupleComparer : IEqualityComparer<(Type, Type)>
        {
            public static TypeTypeValueTupleComparer Instance { get; } = new();
            public bool Equals([AllowNull] (Type, Type) x, [AllowNull] (Type, Type) y) => x.Item1 == y.Item1 && x.Item2 == y.Item2;
            public int GetHashCode([DisallowNull] (Type, Type) obj) => obj.GetHashCode();
        }
    }
}