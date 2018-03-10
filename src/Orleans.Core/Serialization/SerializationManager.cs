using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.ApplicationParts;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Metadata;
using Orleans.Utilities;

namespace Orleans.Serialization
{
    /// <summary>
    /// SerializationManager to oversee the Orleans serializer system.
    /// </summary>
    public sealed class SerializationManager : IDisposable
    {
        #region Privates

        private readonly HashSet<Type> registeredTypes;
        private readonly List<IExternalSerializer> externalSerializers;
        private readonly Dictionary<KeyedSerializerId, IKeyedSerializer> keyedSerializers = new Dictionary<KeyedSerializerId, IKeyedSerializer>();
        private readonly List<IKeyedSerializer> orderedKeyedSerializers = new List<IKeyedSerializer>();
        private readonly ConcurrentDictionary<Type, IExternalSerializer> typeToExternalSerializerDictionary;
        private readonly CachedReadConcurrentDictionary<string, Type> types;
        private readonly Dictionary<string, string> typeKeysToQualifiedNames = new Dictionary<string, string>();
        private readonly Dictionary<RuntimeTypeHandle, DeepCopier> copiers;
        private readonly Dictionary<RuntimeTypeHandle, Serializer> serializers;

        private readonly CachedReadConcurrentDictionary<Type, IKeyedSerializer> typeToKeyedSerializer =
            new CachedReadConcurrentDictionary<Type, IKeyedSerializer>();
        private readonly Dictionary<RuntimeTypeHandle, Deserializer> deserializers;
        private readonly ConcurrentDictionary<Type, Func<GrainReference, GrainReference>> grainRefConstructorDictionary;

        private readonly IExternalSerializer fallbackSerializer;
        private readonly ILogger logger;
        
        // Semi-constants: type handles for simple types
        private static readonly RuntimeTypeHandle shortTypeHandle = typeof(short).TypeHandle;
        private static readonly RuntimeTypeHandle intTypeHandle = typeof(int).TypeHandle;
        private static readonly RuntimeTypeHandle longTypeHandle = typeof(long).TypeHandle;
        private static readonly RuntimeTypeHandle ushortTypeHandle = typeof(ushort).TypeHandle;
        private static readonly RuntimeTypeHandle uintTypeHandle = typeof(uint).TypeHandle;
        private static readonly RuntimeTypeHandle ulongTypeHandle = typeof(ulong).TypeHandle;
        private static readonly RuntimeTypeHandle byteTypeHandle = typeof(byte).TypeHandle;
        private static readonly RuntimeTypeHandle sbyteTypeHandle = typeof(sbyte).TypeHandle;
        private static readonly RuntimeTypeHandle floatTypeHandle = typeof(float).TypeHandle;
        private static readonly RuntimeTypeHandle doubleTypeHandle = typeof(double).TypeHandle;
        private static readonly RuntimeTypeHandle charTypeHandle = typeof(char).TypeHandle;
        private static readonly RuntimeTypeHandle boolTypeHandle = typeof(bool).TypeHandle;
        private static readonly RuntimeTypeHandle objectTypeHandle = typeof(object).TypeHandle;
        private static readonly RuntimeTypeHandle byteArrayTypeHandle = typeof(byte[]).TypeHandle;

        internal readonly CounterStatistic Copies;
        internal readonly CounterStatistic Serializations;
        internal readonly CounterStatistic Deserializations;
        internal readonly CounterStatistic HeaderSers;
        internal readonly CounterStatistic HeaderDesers;
        internal readonly CounterStatistic HeaderSersNumHeaders;
        internal readonly CounterStatistic HeaderDesersNumHeaders;
        internal readonly CounterStatistic CopyTimeStatistic;
        internal readonly CounterStatistic SerTimeStatistic;
        internal readonly CounterStatistic DeserTimeStatistic;
        internal readonly CounterStatistic HeaderSerTime;
        internal readonly CounterStatistic HeaderDeserTime;
        internal readonly IntValueStatistic TotalTimeInSerializer;

        internal readonly CounterStatistic FallbackSerializations;
        internal readonly CounterStatistic FallbackDeserializations;
        internal readonly CounterStatistic FallbackCopies;
        internal readonly CounterStatistic FallbackSerTimeStatistic;
        internal readonly CounterStatistic FallbackDeserTimeStatistic;
        internal readonly CounterStatistic FallbackCopiesTimeStatistic;

        internal int LargeObjectSizeThreshold { get; }

        private readonly ThreadLocal<SerializationContext> serializationContext;
        
        private readonly ThreadLocal<DeserializationContext> deserializationContext;
        private readonly IServiceProvider serviceProvider;
        private readonly ITypeResolver typeResolver;
        private IRuntimeClient runtimeClient;

        internal IRuntimeClient RuntimeClient
            => this.runtimeClient ?? (this.runtimeClient = this.serviceProvider.GetService<IRuntimeClient>());

        internal IServiceProvider ServiceProvider => this.serviceProvider;

        #endregion

        #region initialization

        public SerializationManager(
            IServiceProvider serviceProvider,
            IOptions<SerializationProviderOptions> serializationProviderOptions,
            ILoggerFactory loggerFactory,
            ITypeResolver typeResolver)
        {
            this.LargeObjectSizeThreshold = Constants.LARGE_OBJECT_HEAP_THRESHOLD;
            this.serializationContext = new ThreadLocal<SerializationContext>(() => new SerializationContext(this));
            this.deserializationContext = new ThreadLocal<DeserializationContext>(() => new DeserializationContext(this));

            logger = loggerFactory.CreateLogger<SerializationManager>();
            this.serviceProvider = serviceProvider;
            this.typeResolver = typeResolver;

            registeredTypes = new HashSet<Type>();
            externalSerializers = new List<IExternalSerializer>();
            typeToExternalSerializerDictionary = new ConcurrentDictionary<Type, IExternalSerializer>();
            types = new CachedReadConcurrentDictionary<string, Type>();
            copiers = new Dictionary<RuntimeTypeHandle, DeepCopier>();
            serializers = new Dictionary<RuntimeTypeHandle, Serializer>();
            deserializers = new Dictionary<RuntimeTypeHandle, Deserializer>();
            grainRefConstructorDictionary = new ConcurrentDictionary<Type, Func<GrainReference, GrainReference>>();

            var options = serializationProviderOptions.Value;

            fallbackSerializer = GetFallbackSerializer(serviceProvider, options.FallbackSerializationProvider);

            if (StatisticsCollector.CollectSerializationStats)
            {
                const CounterStorage store = CounterStorage.LogOnly;
                Copies = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_DEEPCOPIES, store);
                Serializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_SERIALIZATION, store);
                Deserializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_DESERIALIZATION, store);
                HeaderSers = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_SERIALIZATION, store);
                HeaderDesers = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_DESERIALIZATION, store);
                HeaderSersNumHeaders = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_SERIALIZATION_NUMHEADERS, store);
                HeaderDesersNumHeaders = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_DESERIALIZATION_NUMHEADERS, store);
                CopyTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_DEEPCOPY_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);
                SerTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_SERIALIZATION_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);
                DeserTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_DESERIALIZATION_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);
                HeaderSerTime = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_SERIALIZATION_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);
                HeaderDeserTime = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_HEADER_DESERIALIZATION_MILLIS, store).AddValueConverter(Utils.TicksToMilliSeconds);

                TotalTimeInSerializer = IntValueStatistic.FindOrCreate(
                    StatisticNames.SERIALIZATION_TOTAL_TIME_IN_SERIALIZER_MILLIS,
                    () =>
                    {
                        long ticks = CopyTimeStatistic.GetCurrentValue() + SerTimeStatistic.GetCurrentValue() + DeserTimeStatistic.GetCurrentValue()
                                     + HeaderSerTime.GetCurrentValue() + HeaderDeserTime.GetCurrentValue();
                        return Utils.TicksToMilliSeconds(ticks);
                    },
                    CounterStorage.LogAndTable);

                const CounterStorage storeFallback = CounterStorage.LogOnly;
                FallbackSerializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_SERIALIZATION, storeFallback);
                FallbackDeserializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DESERIALIZATION, storeFallback);
                FallbackCopies = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DEEPCOPIES, storeFallback);
                FallbackSerTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_SERIALIZATION_MILLIS, storeFallback).AddValueConverter(Utils.TicksToMilliSeconds);
                FallbackDeserTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DESERIALIZATION_MILLIS, storeFallback).AddValueConverter(Utils.TicksToMilliSeconds);
                FallbackCopiesTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DEEPCOPY_MILLIS, storeFallback).AddValueConverter(Utils.TicksToMilliSeconds);
            }

            RegisterSerializationProviders(options.SerializationProviders);
        }

        public void RegisterSerializers(IApplicationPartManager applicationPartManager)
        {
            var serializerFeature = applicationPartManager.CreateAndPopulateFeature<SerializerFeature>();
            this.RegisterSerializers(serializerFeature);

            var grainInterfaceFeature = applicationPartManager.CreateAndPopulateFeature<GrainInterfaceFeature>();
            this.RegisterGrainReferenceSerializers(grainInterfaceFeature);
        }

        private void RegisterGrainReferenceSerializers(GrainInterfaceFeature grainInterfaceFeature)
        {
            foreach (var grainInterface in grainInterfaceFeature.Interfaces)
            {
                this.RegisterGrainReferenceSerializers(grainInterface.ReferenceType);
            }
        }

        private void RegisterSerializers(SerializerFeature serializerFeature)
        {
            var typeSerializer = new BuiltInTypes.DefaultTypeSerializer(this.typeResolver);

            // ReSharper disable once PossibleMistakenCallToGetType.2
            // GetType() returns a RuntimeType, which is an inaccessible subclass of Type which is used at runtime.
            this.Register(typeof(Type).GetType(), typeSerializer.CopyType, typeSerializer.SerializeType, typeSerializer.DeserializeType);
            this.Register(typeof(Type), typeSerializer.CopyType, typeSerializer.SerializeType, typeSerializer.DeserializeType);

            foreach (var serializer in serializerFeature.SerializerDelegates)
            {
                this.Register(serializer.Target, serializer.Delegates.DeepCopy, serializer.Delegates.Serialize, serializer.Delegates.Deserialize);
            }

            foreach (var serializer in serializerFeature.SerializerTypes)
            {
                this.Register(serializer.Target, serializer.Serializer);
            }

            foreach (var knownType in serializerFeature.KnownTypes)
            {
                this.typeKeysToQualifiedNames[knownType.TypeKey] = knownType.Type;
            }
        }

        #endregion

#region Serialization info registration

        /// <summary>
        /// Register a Type with the serialization system to use the specified DeepCopier, Serializer and Deserializer functions.
        /// </summary>
        /// <param name="t">Type to be registered.</param>
        /// <param name="cop">DeepCopier function for this type.</param>
        /// <param name="ser">Serializer function for this type.</param>
        /// <param name="deser">Deserializer function for this type.</param>
        public void Register(Type t, DeepCopier cop, Serializer ser, Deserializer deser)
        {
            Register(t, cop, ser, deser, false);
        }

        private object InitializeSerializer(Type serializerType)
        {
            if (this.ServiceProvider == null)
                return null;

            // If the type lacks a Serializer attribute then it's a self-serializing type and all serialization methods must be static
            if (!serializerType.GetCustomAttributes(typeof(SerializerAttribute), true).Any())
                return null;

            var constructors = serializerType.GetConstructors();
            if (constructors == null || constructors.Length < 1)
                return null;

            var serializer = ActivatorUtilities.GetServiceOrCreateInstance(this.ServiceProvider, serializerType);
            return serializer;
        }

        /// <summary>
        /// Register a Type with the serialization system to use the specified DeepCopier, Serializer and Deserializer functions.
        /// If <c>forcOverride == true</c> then this definition will replace any any previous functions registered for this Type.
        /// </summary>
        /// <param name="t">Type to be registered.</param>
        /// <param name="cop">DeepCopier function for this type.</param>
        /// <param name="ser">Serializer function for this type.</param>
        /// <param name="deser">Deserializer function for this type.</param>
        /// <param name="forceOverride">Whether these functions should replace any previously registered functions for this Type.</param>
        private void Register(Type t, DeepCopier cop, Serializer ser, Deserializer deser, bool forceOverride)
        {
            if ((ser == null) && (deser != null))
            {
                throw new OrleansException("Deserializer without serializer for class " + t.OrleansTypeName());
            }
            if ((ser != null) && (deser == null))
            {
                throw new OrleansException("Serializer without deserializer for class " + t.OrleansTypeName());
            }

            lock (registeredTypes)
            {
                if (registeredTypes.Contains(t))
                {
                    if (cop != null)
                    {
                        lock (copiers)
                        {
                            DeepCopier current;
                            if (forceOverride || !copiers.TryGetValue(t.TypeHandle, out current) || (current == null))
                            {
                                copiers[t.TypeHandle] = cop;
                            }
                        }
                    }
                    if (ser != null)
                    {
                        lock (serializers)
                        {
                            Serializer currentSer;
                            if (forceOverride || !serializers.TryGetValue(t.TypeHandle, out currentSer) || (currentSer == null))
                            {
                                serializers[t.TypeHandle] = ser;
                            }
                        }
                        lock (deserializers)
                        {
                            Deserializer currentDeser;
                            if (forceOverride || !deserializers.TryGetValue(t.TypeHandle, out currentDeser) || (currentDeser == null))
                            {
                                deserializers[t.TypeHandle] = deser;
                            }
                        }
                    }
                }
                else
                {
                    registeredTypes.Add(t);
                    string name = t.OrleansTypeKeyString();
                    types[name] = t;
                    if (cop != null)
                    {
                        lock (copiers)
                        {
                            copiers[t.TypeHandle] = cop;
                        }
                    }
                    if (ser != null)
                    {
                        lock (serializers)
                        {
                            serializers[t.TypeHandle] = ser;
                        }
                    }
                    if (deser != null)
                    {
                        lock (deserializers)
                        {
                            deserializers[t.TypeHandle] = deser;
                        }
                    }

                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Registered type {0} as {1}", t, name);
                }
            }

            // Register any interfaces this type implements, in order to support passing values that are statically of the interface type
            // but dynamically of this (implementation) type
            foreach (var iface in t.GetInterfaces())
            {
                Register(iface);
            }
            // Do the same for abstract base classes
            var baseType = t.GetTypeInfo().BaseType;
            while (baseType != null)
            {
                var baseTypeInfo = baseType.GetTypeInfo();
                if (baseTypeInfo.IsAbstract)
                    Register(baseType);

                baseType = baseTypeInfo.BaseType;
            }
        }

        /// <summary>
        /// This method registers a type that has no specific serializer or deserializer.
        /// For instance, abstract base types and interfaces need to be registered this way.
        /// </summary>
        /// <param name="t">Type to be registered.</param>
        /// <param name="typeKey">Type key to associate with the type.</param>
        private void Register(Type t, string typeKey = null)
        {
            string name = typeKey ?? t.OrleansTypeKeyString();

            lock (registeredTypes)
            {
                if (registeredTypes.Contains(t))
                {
                    return;
                }

                registeredTypes.Add(t);
                types[name] = t;
            }
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Registered type {0} as {1}", t, name);

            // Register any interfaces this type implements, in order to support passing values that are statically of the interface type
            // but dynamically of this (implementation) type
            foreach (var iface in t.GetInterfaces())
            {
                Register(iface);
            }

            // Do the same for abstract base classes
            var baseType = t.GetTypeInfo().BaseType;
            while (baseType != null)
            {
                var baseTypeInfo = baseType.GetTypeInfo();
                if (baseTypeInfo.IsAbstract)
                    Register(baseType);

                baseType = baseTypeInfo.BaseType;
            }
        }

        /// <summary>
        /// Registers <paramref name="serializerType"/> as the serializer for <paramref name="type"/>.
        /// </summary>
        /// <param name="type">The type serialized by the provided serializer type.</param>
        /// <param name="serializerType">The type containing serialization methods for <paramref name="type"/>.</param>
        private void Register(Type type, Type serializerType)
        {
            GetSerializationMethods(serializerType, out var copier, out var serializer, out var deserializer);

            if (serializer == null && deserializer == null && copier == null)
            {
                var msg = $"No serialization methods found on type {serializerType.GetParseableName(TypeFormattingOptions.LogFormat)}.";
                logger.Warn(
                    ErrorCode.SerMgr_SerializationMethodsMissing,
                    msg);
                throw new ArgumentException(msg);
            }

            if (serializer != null ^ deserializer != null)
            {
                var msg =
                    $"Inconsistency between serializer and deserializer methods on type {serializerType.GetParseableName(TypeFormattingOptions.LogFormat)}."
                    + " Either both must be specified or both must be null. "
                    + $"Instead, serializer = {serializer?.ToString() ?? "null"} and deserializer = {deserializer?.ToString() ?? "null"}";
                logger.Warn(
                    ErrorCode.SerMgr_SerializationMethodsMissing,
                    msg);
                throw new ArgumentException(msg);
            }

            try
            {
                if (type.GetTypeInfo().IsGenericTypeDefinition)
                {
                    object DeepCopyGeneric(object obj, ICopyContext context)
                    {
                        var concrete = this.RegisterConcreteSerializer(obj.GetType(), serializerType);
                        return concrete.DeepCopy(obj, context);
                    }

                    void SerializeGeneric(object obj, ISerializationContext context, Type exp)
                    {
                        var concrete = this.RegisterConcreteSerializer(obj.GetType(), serializerType);
                        concrete.Serialize(obj, context, exp);
                    }

                    object DeserializeGeneric(Type expected, IDeserializationContext context)
                    {
                        var concrete = this.RegisterConcreteSerializer(expected, serializerType);
                        return concrete.Deserialize(expected, context);
                    }

                    this.Register(
                        type,
                        copier != null ? DeepCopyGeneric : default(DeepCopier),
                        serializer != null ? SerializeGeneric : default(Serializer),
                        deserializer != null ? DeserializeGeneric : default(Deserializer),
                        true);
                }
                else
                {
                    var serializerInstance = this.InitializeSerializer(serializerType);
                    this.Register(
                        type,
                        CreateDelegate<DeepCopier>(copier, serializerInstance),
                        CreateDelegate<Serializer>(serializer, serializerInstance),
                        CreateDelegate<Deserializer>(deserializer, serializerInstance),
                        true);
                }
            }
            catch (ArgumentException)
            {
                logger.Warn(
                    ErrorCode.SerMgr_ErrorBindingMethods,
                    "Error binding serialization methods for type {0}",
                    type.GetParseableName());
                throw;
            }

            if (this.logger.IsEnabled(LogLevel.Trace))
                this.logger.Trace(
                    "Loaded serialization info for type {0} from assembly {1}",
                    type.Name,
                    serializerType.Assembly.GetName().Name);
        }
        
        /// <summary>
        /// Registers <see cref="GrainReference"/> serializers for the provided <paramref name="type"/>.
        /// </summary>
        /// <param name="type">
        /// The type.
        /// </param>
        private void RegisterGrainReferenceSerializers(Type type)
        {
            var attr = type.GetTypeInfo().GetCustomAttribute<GrainReferenceAttribute>();
            if (attr?.TargetType == null)
            {
                return;
            }

            var defaultCtorDelegate = CreateGrainRefConstructorDelegate(type, null);

            // Register GrainReference serialization methods.
            Register(
                type,
                GrainReferenceSerializer.CopyGrainReference,
                GrainReferenceSerializer.SerializeGrainReference,
                (expected, context) =>
                {
                    Func<GrainReference, GrainReference> ctorDelegate;
                    var deserialized = (GrainReference)GrainReferenceSerializer.DeserializeGrainReference(expected, context);
                    if (expected.IsConstructedGenericType == false)
                    {
                        return defaultCtorDelegate(deserialized);
                    }

                    if (!grainRefConstructorDictionary.TryGetValue(expected, out ctorDelegate))
                    {
                        ctorDelegate = CreateGrainRefConstructorDelegate(type, expected.GenericTypeArguments);
                        grainRefConstructorDictionary.TryAdd(expected, ctorDelegate);
                    }

                    return ctorDelegate(deserialized);
                });
        }

        private static Func<GrainReference, GrainReference> CreateGrainRefConstructorDelegate(Type type, Type[] genericArgs)
        {
            TypeInfo typeInfo = type.GetTypeInfo();
            if (typeInfo.IsGenericType)
            {
                if (type.IsConstructedGenericType == false && genericArgs == null)
                {
                    return null;
                }

                type = type.MakeGenericType(genericArgs);
            }

            var constructor = TypeUtils.GetConstructorThatMatches(type, new[] { typeof(GrainReference) });
            var method = new DynamicMethod(
                ".ctor_" + type.Name,
                typeof(GrainReference),
                new[] { typeof(GrainReference) },
                typeof(SerializationManager).GetTypeInfo().Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Ret);
            return
                (Func<GrainReference, GrainReference>)
                method.CreateDelegate(typeof(Func<GrainReference, GrainReference>));
        }


        private SerializerMethods RegisterConcreteSerializer(Type concreteType, Type genericSerializerType)
        {
            MethodInfo copier;
            MethodInfo serializer;
            MethodInfo deserializer;

            var concreteSerializerType = genericSerializerType.MakeGenericType(concreteType.GetGenericArguments());
            var typeAlreadyRegistered = false;
            
            lock (registeredTypes)
            {
                typeAlreadyRegistered = registeredTypes.Contains(concreteSerializerType);
            }
            
            if (typeAlreadyRegistered)
            {
                return new SerializerMethods(
                    GetCopier(concreteSerializerType),
                    GetSerializer(concreteSerializerType),
                    GetDeserializer(concreteSerializerType));
            }

            GetSerializationMethods(concreteSerializerType, out copier, out serializer, out deserializer);
            var serializerInstance = this.InitializeSerializer(concreteSerializerType);
            var concreteCopier = CreateDelegate<DeepCopier>(copier, serializerInstance);
            var concreteSerializer = CreateDelegate<Serializer>(serializer, serializerInstance);
            var concreteDeserializer = CreateDelegate<Deserializer>(deserializer, serializerInstance);

            this.Register(concreteType, concreteCopier, concreteSerializer, concreteDeserializer, true);

            return new SerializerMethods(concreteCopier, concreteSerializer, concreteDeserializer);
        }

        internal static void GetSerializationMethods(Type type, out MethodInfo copier, out MethodInfo serializer, out MethodInfo deserializer)
        {
            copier = null;
            serializer = null;
            deserializer = null;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttributes(typeof(CopierMethodAttribute), true).Any())
                {
                    copier = method;
                }
                else if (method.GetCustomAttributes(typeof(SerializerMethodAttribute), true).Any())
                {
                    serializer = method;
                }
                else if (method.GetCustomAttributes(typeof(DeserializerMethodAttribute), true).Any())
                {
                    deserializer = method;
                }
            }
        }

        private T CreateDelegate<T>(MethodInfo methodInfo, object target) where T : class
        {
            if (this.ServiceProvider == null)
                return default(T);

            if (methodInfo == null)
                return default(T);

            return (methodInfo.IsStatic
                ? methodInfo.CreateDelegate(typeof(T))
                : methodInfo.CreateDelegate(typeof(T), target)) as T;
        }

        #endregion

        #region Deep copying

        internal DeepCopier GetCopier(Type t)
        {
            lock (copiers)
            {
                DeepCopier copier;
                if (copiers.TryGetValue(t.TypeHandle, out copier))
                    return copier;

                var typeInfo = t.GetTypeInfo();
                if (typeInfo.IsGenericType && copiers.TryGetValue(typeInfo.GetGenericTypeDefinition().TypeHandle, out copier))
                    return copier;
            }

            return null;
        }

        /// <summary>
        /// Deep copy the specified object, using DeepCopier functions previously registered for this type.
        /// </summary>
        /// <param name="original">The input data to be deep copied.</param>
        /// <returns>Deep copied clone of the original input object.</returns>
        public object DeepCopy(object original)
        {
            var context = this.serializationContext.Value;
            context.Reset();
            
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                context.SerializationManager.Copies.Increment();
            }

            object copy = DeepCopyInner(original, context);
            context.Reset();
            

            if (timer!=null)
            {
                timer.Stop();
                context.SerializationManager.CopyTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
            
            return copy;
        }

        /// <summary>
        /// <para>
        /// This method makes a deep copy of the object passed to it.
        /// </para>
        /// </summary>
        /// <param name="original">The input data to be deep copied.</param>
        /// <param name="context">The context.</param>
        /// <returns>Deep copied clone of the original input object.</returns>
        public static object DeepCopyInner(object original, ICopyContext context)
        {
            if (original == null) return null;
            var sm = context.GetSerializationManager();

            var t = original.GetType();
            var shallow = t.IsOrleansShallowCopyable();

            if (shallow)
                return original;

            var reference = context.CheckObjectWhileCopying(original);
            if (reference != null)
                return reference;

            object copy;

            IExternalSerializer serializer;
            if (sm.TryLookupExternalSerializer(t, out serializer))
            {
                copy = serializer.DeepCopy(original, context);
                context.RecordCopy(original, copy);
                return copy;
            }

            var copier = sm.GetCopier(t);
            if (copier != null)
            {
                copy = copier(original, context);
                context.RecordCopy(original, copy);
                return copy;
            }

            return sm.DeepCopierHelper(t, original, context);
        }

        private object DeepCopierHelper(Type t, object original, ICopyContext context)
        {
            // Arrays are all that's left. 
            // Handling arbitrary-rank arrays is a bit complex, but why not?
            var originalArray = original as Array;
            if (originalArray != null)
            {
                if (originalArray.Rank == 1 && originalArray.GetLength(0) == 0)
                {
                    // A common special case - empty one dimensional array
                    return originalArray;
                }
                // A common special case
                if (t.TypeHandle.Equals(byteArrayTypeHandle) && (originalArray.Rank == 1))
                {
                    var source = (byte[])original;
                    if (source.Length > this.LargeObjectSizeThreshold)
                    {
                        logger.Info(ErrorCode.Ser_LargeObjectAllocated,
                            "Large byte array of size {0} is being copied. This will result in an allocation on the large object heap. " +
                            "Frequent allocations to the large object heap can result in frequent gen2 garbage collections and poor system performance. " +
                            "Please consider using Immutable<byte[]> instead.", source.Length);
                    }
                    var dest = new byte[source.Length];
                    Array.Copy(source, dest, source.Length);
                    return dest;
                }

                var et = t.GetElementType();
                var etInfo = et.GetTypeInfo();
                if (et.IsOrleansShallowCopyable())
                {
                    // Only check the size for primitive types because otherwise Buffer.ByteLength throws
                    if (etInfo.IsPrimitive && Buffer.ByteLength(originalArray) > this.LargeObjectSizeThreshold)
                    {
                        logger.Info(ErrorCode.Ser_LargeObjectAllocated,
                            $"Large {t.OrleansTypeName()} array of total byte size {Buffer.ByteLength(originalArray)} is being copied. This will result in an allocation on the large object heap. " +
                            "Frequent allocations to the large object heap can result in frequent gen2 garbage collections and poor system performance. " +
                            $"Please consider using Immutable<{t.OrleansTypeName()}> instead.");
                    }
                    return originalArray.Clone();
                }

                // We assume that all arrays have lower bound 0. In .NET 4.0, it's hard to create an array with a non-zero lower bound.
                var rank = originalArray.Rank;
                var lengths = new int[rank];
                for (var i = 0; i < rank; i++)
                    lengths[i] = originalArray.GetLength(i);

                var copyArray = Array.CreateInstance(et, lengths);
                context.RecordCopy(original, copyArray);

                if (rank == 1)
                {
                    for (var i = 0; i < lengths[0]; i++)
                        copyArray.SetValue(DeepCopyInner(originalArray.GetValue(i), context), i);
                }
                else if (rank == 2)
                {
                    for (var i = 0; i < lengths[0]; i++)
                        for (var j = 0; j < lengths[1]; j++)
                            copyArray.SetValue(DeepCopyInner(originalArray.GetValue(i, j), context), i, j);
                }
                else
                {
                    var index = new int[rank];
                    var sizes = new int[rank];
                    sizes[rank - 1] = 1;
                    for (var k = rank - 2; k >= 0; k--)
                        sizes[k] = sizes[k + 1]*lengths[k + 1];

                    for (var i = 0; i < originalArray.Length; i++)
                    {
                        int k = i;
                        for (int n = 0; n < rank; n++)
                        {
                            int offset = k / sizes[n];
                            k = k - offset * sizes[n];
                            index[n] = offset;
                        }
                        copyArray.SetValue(DeepCopyInner(originalArray.GetValue(index), context), index);
                    }
                }
                return copyArray;

            }

            IKeyedSerializer keyedSerializer;
            if (this.TryLookupKeyedSerializer(t, out keyedSerializer))
            {
                return keyedSerializer.DeepCopy(original, context);
            }

            if (fallbackSerializer.IsSupportedType(t))
                return FallbackSerializationDeepCopy(original, context);

            throw new OrleansException("No copier found for object of type " + t.OrleansTypeName() + 
                ". Perhaps you need to mark it [Serializable] or define a custom serializer for it?");
        }

#endregion

#region Serializing

        /// <summary>
        /// Returns true if <paramref name="t"/> is serializable, false otherwise.
        /// </summary>
        /// <param name="t">The type.</param>
        /// <returns>true if <paramref name="t"/> is serializable, false otherwise.</returns>
        internal bool HasSerializer(Type t)
        {
            lock (serializers)
            {
                Serializer ser;
                if (serializers.TryGetValue(t.TypeHandle, out ser)) return true;
                var typeInfo = t.GetTypeInfo();
                if (typeInfo.IsOrleansPrimitive()) return true;
                if (!typeInfo.IsGenericType) return false;
                var genericTypeDefinition = typeInfo.GetGenericTypeDefinition();
                return serializers.TryGetValue(genericTypeDefinition.TypeHandle, out ser) &&
                       typeInfo.GetGenericArguments().All(type => HasSerializer(type));
            }
        }

        internal Serializer GetSerializer(Type t)
        {
            lock (serializers)
            {
                Serializer ser;
                if (serializers.TryGetValue(t.TypeHandle, out ser))
                    return ser;

                var typeInfo = t.GetTypeInfo();
                if (typeInfo.IsGenericType)
                    if (serializers.TryGetValue(typeInfo.GetGenericTypeDefinition().TypeHandle, out ser))
                        return ser;
            }

            return null;
        }

        /// <summary>
        /// Serialize the specified object, using Serializer functions previously registered for this type.
        /// </summary>
        /// <param name="raw">The input data to be serialized.</param>
        /// <param name="stream">The output stream to write to.</param>
        public void Serialize(object raw, IBinaryTokenStreamWriter stream)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                Serializations.Increment();
            }

            var context = this.serializationContext.Value;
            context.Reset();
            context.StreamWriter = stream;
            SerializeInner(raw, context, null);
            context.Reset();
            
            if (timer!=null)
            {
                timer.Stop();
                SerTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
        }

        /// <summary>
        /// Encodes the object to the provided binary token stream.
        /// </summary>
        /// <param name="obj">The input data to be serialized.</param>
        /// <param name="context">The serialization context.</param>
        public static void SerializeInner<T>(T obj, ISerializationContext context) => SerializeInner(obj, context, typeof(T));

        /// <summary>
        /// Encodes the object to the provided binary token stream.
        /// </summary>
        /// <param name="obj">The input data to be serialized.</param>
        /// <param name="context">The serialization context.</param>
        /// <param name="expected">Current expected Type on this stream.</param>
        [SuppressMessage("Microsoft.Usage", "CA2201:DoNotRaiseReservedExceptionTypes")]
        public static void SerializeInner(object obj, ISerializationContext context, Type expected)
        {
            var sm = context.GetSerializationManager();
            var writer = context.StreamWriter;

            // Nulls get special handling
            if (obj == null)
            {
                writer.WriteNull();
                return;
            }

            var t = obj.GetType();
            var typeInfo = t.GetTypeInfo();

            // Enums are extra-special
            if (typeInfo.IsEnum)
            {
                writer.WriteTypeHeader(t, expected);
                WriteEnum(obj, writer, t);

                return;
            }

            // Check for simple types
            if (writer.TryWriteSimpleObject(obj)) return;

            // Check for primitives
            // At this point, we're either an object or a non-trivial value type

            // Start by checking to see if we're a back-reference, and recording us for possible future back-references if not
            if (!typeInfo.IsValueType)
            {
                int reference = context.CheckObjectWhileSerializing(obj);
                if (reference >= 0)
                {
                    writer.WriteReference(reference);
                    return;
                }

                context.RecordObject(obj);
            }

            // If we're simply a plain old unadorned, undifferentiated object, life is easy
            if (t.TypeHandle.Equals(objectTypeHandle))
            {
                writer.Write(SerializationTokenType.SpecifiedType);
                writer.Write(SerializationTokenType.Object);
                return;
            }

            // Arrays get handled specially
            if (typeInfo.IsArray)
            {
                var et = t.GetElementType();
                SerializeArray((Array)obj, context, expected, et);
                return;
            }

            IExternalSerializer serializer;
            if (sm.TryLookupExternalSerializer(t, out serializer))
            {
                writer.WriteTypeHeader(t, expected);
                serializer.Serialize(obj, context, expected);
                return;
            }

            Serializer ser = sm.GetSerializer(t);
            if (ser != null)
            {
                writer.WriteTypeHeader(t, expected);
                ser(obj, context, expected);
                return;
            }

            if (sm.TryLookupKeyedSerializer(t, out var keyedSerializer))
            {
                writer.Write((byte)SerializationTokenType.KeyedSerializer);
                writer.Write((byte)keyedSerializer.SerializerId);
                keyedSerializer.Serialize(obj, context, expected);
                return;
            }

            if (sm.fallbackSerializer.IsSupportedType(t))
            {
                sm.FallbackSerializer(obj, context, expected);
                return;
            }

            if (obj is Exception && !sm.fallbackSerializer.IsSupportedType(t))
            {
                // Exceptions should always be serializable, and thus handled by the prior if.
                // In case someone creates a non-serializable exception, though, we don't want to 
                // throw and return a serialization exception...
                // Note that the "!t.IsSerializable" is redundant in this if, but it's there in case
                // this code block moves.
                var rawException = obj as Exception;
                var foo = new Exception(String.Format("Non-serializable exception of type {0}: {1}" + Environment.NewLine + "at {2}",
                                                      t.OrleansTypeName(), rawException.Message,
                                                      rawException.StackTrace));
                sm.FallbackSerializer(foo, context, expected);
                return;
            }

            throw new ArgumentException(
                "No serializer found for object of type " + t.OrleansTypeKeyString() + " from assembly " + t.Assembly.FullName
                + ". Perhaps you need to mark it [Serializable] or define a custom serializer for it?");
        }

        private static void WriteEnum(object obj, IBinaryTokenStreamWriter stream, Type type)
        {
            var t = Enum.GetUnderlyingType(type).TypeHandle;
            if (t.Equals(byteTypeHandle) || t.Equals(sbyteTypeHandle)) stream.Write(Convert.ToByte(obj));
            else if (t.Equals(shortTypeHandle) || t.Equals(ushortTypeHandle)) stream.Write(Convert.ToInt16(obj));
            else if (t.Equals(intTypeHandle) || t.Equals(uintTypeHandle)) stream.Write(Convert.ToInt32(obj));
            else if (t.Equals(longTypeHandle) || t.Equals(ulongTypeHandle)) stream.Write(Convert.ToInt64(obj));
            else throw new NotSupportedException($"Serialization of type {type.GetParseableName()} is not supported.");
        }

        private static object ReadEnum(IBinaryTokenStreamReader stream, Type type)
        {
            var t = Enum.GetUnderlyingType(type).TypeHandle;
            if (t.Equals(byteTypeHandle) || t.Equals(sbyteTypeHandle)) return Enum.ToObject(type, stream.ReadByte());
            if (t.Equals(shortTypeHandle) || t.Equals(ushortTypeHandle)) return Enum.ToObject(type, stream.ReadShort());
            if (t.Equals(intTypeHandle) || t.Equals(uintTypeHandle)) return Enum.ToObject(type, stream.ReadInt());
            if (t.Equals(longTypeHandle) || t.Equals(ulongTypeHandle)) return Enum.ToObject(type, stream.ReadLong());
            throw new NotSupportedException($"Deserialization of type {type.GetParseableName()} is not supported.");
        }

        // We assume that all lower bounds are 0, since creating an array with lower bound !=0 is hard in .NET 4.0+
        private static void SerializeArray(Array array, ISerializationContext context, Type expected, Type et)
        {
            var writer = context.StreamWriter;

            // First check for one of the optimized cases
            if (array.Rank == 1)
            {
                if (et.TypeHandle.Equals(byteTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.ByteArray);
                    writer.Write(array.Length);
                    writer.Write((byte[])array);
                    return;
                }
                if (et.TypeHandle.Equals(sbyteTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.SByteArray);
                    writer.Write(array.Length);
                    writer.Write((sbyte[])array);
                    return;
                }
                if (et.TypeHandle.Equals(boolTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.BoolArray);
                    writer.Write(array.Length);
                    writer.Write((bool[])array);
                    return;
                }
                if (et.TypeHandle.Equals(charTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.CharArray);
                    writer.Write(array.Length);
                    writer.Write((char[])array);
                    return;
                }
                if (et.TypeHandle.Equals(shortTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.ShortArray);
                    writer.Write(array.Length);
                    writer.Write((short[])array);
                    return;
                }
                if (et.TypeHandle.Equals(intTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.IntArray);
                    writer.Write(array.Length);
                    writer.Write((int[])array);
                    return;
                }
                if (et.TypeHandle.Equals(longTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.LongArray);
                    writer.Write(array.Length);
                    writer.Write((long[])array);
                    return;
                }
                if (et.TypeHandle.Equals(ushortTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.UShortArray);
                    writer.Write(array.Length);
                    writer.Write((ushort[])array);
                    return;
                }
                if (et.TypeHandle.Equals(uintTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.UIntArray);
                    writer.Write(array.Length);
                    writer.Write((uint[])array);
                    return;
                }
                if (et.TypeHandle.Equals(ulongTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.ULongArray);
                    writer.Write(array.Length);
                    writer.Write((ulong[])array);
                    return;
                }
                if (et.TypeHandle.Equals(floatTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.FloatArray);
                    writer.Write(array.Length);
                    writer.Write((float[])array);
                    return;
                }
                if (et.TypeHandle.Equals(doubleTypeHandle))
                {
                    writer.Write(SerializationTokenType.SpecifiedType);
                    writer.Write(SerializationTokenType.DoubleArray);
                    writer.Write(array.Length);
                    writer.Write((double[])array);
                    return;
                }
            }

            // Write the array header
            writer.WriteArrayHeader(array, expected);

            // Figure out the array size
            var rank = array.Rank;
            var lengths = new int[rank];
            for (var i = 0; i < rank; i++)
            {
                lengths[i] = array.GetLength(i);
            }

            if (rank == 1)
            {
                for (int i = 0; i < lengths[0]; i++)
                    SerializeInner(array.GetValue(i), context, et);
            }
            else if (rank == 2)
            {
                for (int i = 0; i < lengths[0]; i++)
                    for (int j = 0; j < lengths[1]; j++)
                        SerializeInner(array.GetValue(i, j), context, et);
            }
            else
            {
                var index = new int[rank];
                var sizes = new int[rank];
                sizes[rank - 1] = 1;
                for (var k = rank - 2; k >= 0; k--)
                    sizes[k] = sizes[k + 1]*lengths[k + 1];

                for (var i = 0; i < array.Length; i++)
                {
                    int k = i;
                    for (int n = 0; n < rank; n++)
                    {
                        int offset = k / sizes[n];
                        k = k - offset * sizes[n];
                        index[n] = offset;
                    }
                    SerializeInner(array.GetValue(index), context, et);
                }
            }
        }

        /// <summary>
        /// Serialize data into byte[].
        /// </summary>
        /// <param name="raw">Input data.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public byte[] SerializeToByteArray(object raw)
        {
            var stream = new BinaryTokenStreamWriter();
            byte[] result;
            try
            {
                var context = this.serializationContext.Value;
                context.Reset();
                context.StreamWriter = stream;
                SerializeInner(raw, context, null);
                context.Reset();
                result = stream.ToByteArray();
            }
            finally
            {
                stream.ReleaseBuffers();
            }
            return result;
        }

#endregion

#region Deserializing

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public object Deserialize(IBinaryTokenStreamReader stream)
        {
            return this.Deserialize(null, stream);
        }

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <typeparam name="T">Type to return.</typeparam>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public T Deserialize<T>(IBinaryTokenStreamReader stream)
        {
            return (T)this.Deserialize(typeof(T), stream);
        }

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <param name="t">Type to return.</param>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public object Deserialize(Type t, IBinaryTokenStreamReader stream)
        {
            var context = this.deserializationContext.Value;
            context.Reset();
            context.StreamReader = stream;
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                context.SerializationManager.Deserializations.Increment();
            }
            object result = null;

            result = DeserializeInner(t, context);
            context.Reset();
            
            if (timer!=null)
            {
                timer.Stop();
                context.SerializationManager.DeserTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
            return result;
        }

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <typeparam name="T">Type to return.</typeparam>
        /// <param name="context">Deserialization context.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public static T DeserializeInner<T>(IDeserializationContext context)
        {
            return (T)DeserializeInner(typeof(T), context);
        }

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <param name="expected">Type to return.</param>
        /// <param name="context">The deserialization context.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public static object DeserializeInner(Type expected, IDeserializationContext context)
        {
            var sm = context.GetSerializationManager();
            var previousOffset = context.CurrentObjectOffset;
            var reader = context.StreamReader;
            context.CurrentObjectOffset = context.CurrentPosition;

            try
            {
                // NOTE: we don't check that the actual dynamic result implements the expected type.
                // We'll allow a cast exception higher up to catch this.
                SerializationTokenType token;
                object result;
                if (reader.TryReadSimpleType(out result, out token))
                {
                    return result;
                }

                // Special serializations (reference, fallback)
                if (token == SerializationTokenType.Reference)
                {
                    var offset = reader.ReadInt();
                    result = context.FetchReferencedObject(offset);
                    return result;
                }
                if (token == SerializationTokenType.Fallback)
                {
                    var fallbackResult = sm.FallbackDeserializer(context, expected);
                    context.RecordObject(fallbackResult);
                    return fallbackResult;
                }

                Type resultType;
                if (token == SerializationTokenType.ExpectedType)
                {
                    if (expected == null)
                    {
                        throw new SerializationException("ExpectedType token encountered but no expected type provided");
                    }

                    resultType = expected;
                }
                else if (token == SerializationTokenType.SpecifiedType)
                {
                    resultType = reader.ReadSpecifiedTypeHeader(sm);
                }
                else if (token == SerializationTokenType.KeyedSerializer)
                {
                    var serializerId = (KeyedSerializerId)reader.ReadByte();
                    if (!sm.keyedSerializers.TryGetValue(serializerId, out var keyedSerializer))
                    {
                        throw new SerializationException($"Specified serializer {serializerId} not configured.");
                    }

                    return keyedSerializer.Deserialize(expected, context);
                }
                else
                {
                    throw new SerializationException("Unexpected token '" + token + "' introducing type specifier");
                }

                // Handle object, which is easy
                if (resultType.TypeHandle.Equals(objectTypeHandle))
                {
                    return new object();
                }

                var resultTypeInfo = resultType.GetTypeInfo();
                // Handle enums
                if (resultTypeInfo.IsEnum)
                {
                    result = ReadEnum(reader, resultType);
                    return result;
                }

                if (resultTypeInfo.IsArray)
                {
                    result = DeserializeArray(resultType, context);
                    context.RecordObject(result);
                    return result;
                }

                IExternalSerializer serializer;
                if (sm.TryLookupExternalSerializer(resultType, out serializer))
                {
                    result = serializer.Deserialize(resultType, context);
                    context.RecordObject(result);
                    return result;
                }

                var deser = sm.GetDeserializer(resultType);
                if (deser != null)
                {
                    result = deser(resultType, context);
                    context.RecordObject(result);
                    return result;
                }

                throw new SerializationException(
                    "Unsupported type '" + resultType.OrleansTypeName()
                    + "' encountered. Perhaps you need to mark it [Serializable] or define a custom serializer for it?");
            }
            finally
            {
                context.CurrentObjectOffset = previousOffset;
            }
        }

        private static object DeserializeArray(Type resultType, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            var lengths = ReadArrayLengths(resultType.GetArrayRank(), reader);
            var rank = lengths.Length;
            var et = resultType.GetElementType();

            // Optimized special cases
            if (rank == 1)
            {
                if (et.TypeHandle.Equals(byteTypeHandle))
                    return reader.ReadBytes(lengths[0]);

                if (et.TypeHandle.Equals(sbyteTypeHandle))
                {
                    var result = new sbyte[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(shortTypeHandle))
                {
                    var result = new short[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(intTypeHandle))
                {
                    var result = new int[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(longTypeHandle))
                {
                    var result = new long[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(ushortTypeHandle))
                {
                    var result = new ushort[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(uintTypeHandle))
                {
                    var result = new uint[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(ulongTypeHandle))
                {
                    var result = new ulong[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(doubleTypeHandle))
                {
                    var result = new double[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(floatTypeHandle))
                {
                    var result = new float[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(charTypeHandle))
                {
                    var result = new char[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(boolTypeHandle))
                {
                    var result = new bool[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    reader.ReadBlockInto(result, n);
                    return result;
                }
            }

            var array = Array.CreateInstance(et, lengths);

            if (rank == 1)
            {
                for (int i = 0; i < lengths[0]; i++)
                    array.SetValue(DeserializeInner(et, context), i);
            }
            else if (rank == 2)
            {
                for (int i = 0; i < lengths[0]; i++)
                    for (int j = 0; j < lengths[1]; j++)
                        array.SetValue(DeserializeInner(et, context), i, j);
            }
            else
            {
                var index = new int[rank];
                var sizes = new int[rank];
                sizes[rank - 1] = 1;
                for (var k = rank - 2; k >= 0; k--)
                    sizes[k] = sizes[k + 1]*lengths[k + 1];

                for (var i = 0; i < array.Length; i++)
                {
                    int k = i;
                    for (int n = 0; n < rank; n++)
                    {
                        int offset = k / sizes[n];
                        k = k - offset * sizes[n];
                        index[n] = offset;
                    }
                    array.SetValue(DeserializeInner(et, context), index);
                }
            }

            return array;
        }

        private static int[] ReadArrayLengths(int n, IBinaryTokenStreamReader stream)
        {
            var result = new int[n];
            for (var i = 0; i < n; i++)
                result[i] = stream.ReadInt();

            return result;
        }

        internal Deserializer GetDeserializer(Type t)
        {
            Deserializer deser;

            lock (deserializers)
            {
                if (deserializers.TryGetValue(t.TypeHandle, out deser))
                    return deser;
            }

            if (t.GetTypeInfo().IsGenericType)
            {
                lock (deserializers)
                {
                    if (deserializers.TryGetValue(t.GetGenericTypeDefinition().TypeHandle, out deser))
                        return deser;
                }
            }

            return null;
        }

        /// <summary>
        /// Deserialize data from the specified byte[] and rehydrate backi into objects.
        /// </summary>
        /// <typeparam name="T">Type of data to be returned.</typeparam>
        /// <param name="data">Input data.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public T DeserializeFromByteArray<T>(byte[] data)
        {
            var context = this.deserializationContext.Value;
            context.Reset();
            context.StreamReader = new BinaryTokenStreamReader(data);
            var result = DeserializeInner<T>(context);
            context.Reset();
            return result;
        }

#endregion

#region Special case code for message headers

        internal static void SerializeMessageHeaders(Message.HeadersContainer headers, SerializationContext context)
        {
            var sm = context.SerializationManager;
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
            }

            var ser = sm.GetSerializer(typeof(Message.HeadersContainer));
            ser(headers, context, typeof(Message.HeadersContainer));

            if (timer != null)
            {
                timer.Stop();
                sm.HeaderSers.Increment();
                sm.HeaderSerTime.IncrementBy(timer.ElapsedTicks);
            }
        }

        internal static Message.HeadersContainer DeserializeMessageHeaders(IDeserializationContext context)
        {
            var sm = context.GetSerializationManager();
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
            }

             var des = sm.GetDeserializer(typeof(Message.HeadersContainer));
             var headers = (Message.HeadersContainer)des(typeof(Message.HeadersContainer), context);

            if (timer != null)
            {
                timer.Stop();
                sm.HeaderDesers.Increment();
                sm.HeaderDeserTime.IncrementBy(timer.ElapsedTicks);
            }

            return headers;
        }
        
        private bool TryLookupExternalSerializer(Type t, out IExternalSerializer serializer)
        {
            // essentially a no-op if there are no external serializers registered
            if (externalSerializers.Count == 0)
            {
                serializer = null;
                return false;
            }

            // the associated serializer will be null if there are no external serializers that handle this type
            if (typeToExternalSerializerDictionary.TryGetValue(t, out serializer))
            {
                return serializer != null;
            }
                      
            serializer = externalSerializers.FirstOrDefault(s => s.IsSupportedType(t));

            // add the serializer to the dictionary, even if it's null to signify that we already performed
            // the search and found none
            if (typeToExternalSerializerDictionary.TryAdd(t, serializer) && serializer != null)
            {
                // we need to register the type, otherwise exceptions are thrown about types not being found
                Register(t, serializer.DeepCopy, serializer.Serialize, serializer.Deserialize, true);
            }
   
            return serializer != null;
        }

        private bool TryLookupKeyedSerializer(Type type, out IKeyedSerializer serializer)
        {
            if (this.orderedKeyedSerializers.Count == 0)
            {
                serializer = null;
                return false;
            }

            if (this.typeToKeyedSerializer.TryGetValue(type, out serializer)) return true;

            foreach (var keyedSerializer in this.orderedKeyedSerializers)
            {
                if (keyedSerializer.IsSupportedType(type))
                {
                    this.typeToKeyedSerializer[type] = keyedSerializer;
                    serializer = keyedSerializer;
                    return true;
                }
            }

            return false;
        }

#endregion

#region Fallback serializer and deserializer

        internal void FallbackSerializer(object raw, ISerializationContext context, Type t)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                FallbackSerializations.Increment();
            }

            context.StreamWriter.Write(SerializationTokenType.Fallback);
            fallbackSerializer.Serialize(raw, context, t);

            if (StatisticsCollector.CollectSerializationStats)
            {
                timer.Stop();
                FallbackSerTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
        }

        private object FallbackDeserializer(IDeserializationContext context, Type expectedType)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                FallbackDeserializations.Increment();
            }
            var retVal = fallbackSerializer.Deserialize(expectedType, context);
            if (timer != null)
            {
                timer.Stop();
                FallbackDeserTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }

            return retVal;
        }

        private IExternalSerializer GetFallbackSerializer(IServiceProvider serviceProvider, TypeInfo fallbackType)
        {
            IExternalSerializer serializer;
            if (fallbackType != null)
            {
                serializer = (IExternalSerializer)ActivatorUtilities.CreateInstance(serviceProvider, fallbackType.AsType());
            }
            else
            {
                serializer = new BinaryFormatterSerializer();
            }
            return serializer;
        }

        private object FallbackSerializationDeepCopy(object obj, ICopyContext context)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                FallbackCopies.Increment();
            }

            var retVal = fallbackSerializer.DeepCopy(obj, context);
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer.Stop();
                FallbackCopiesTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
            return retVal;
        }

#endregion

#region Utilities

        internal Type ResolveTypeName(string typeName)
        {
            if (types.TryGetValue(typeName, out var result))
            {
                return result;
            }

            // Attempt to find the qualified type name from the key and resolve the type from the name.
            if (this.typeKeysToQualifiedNames.TryGetValue(typeName, out var fullyQualifiedName) && this.typeResolver.TryResolveType(fullyQualifiedName, out result))
            {
                this.types[typeName] = result;
                return result;
            }

            if (typeName[typeName.Length - 1] == ']')
            {
                // It's an array type declaration: elementType[,,,]
                var j = typeName.LastIndexOf('[');
                // The rank of the array will be the length of the string, minus the index of the [, minus 1; it's the number of commas between the [ and the ]
                var rank = typeName.Length - j - 1;
                var baseName = typeName.Substring(0, j);
                var baseType = ResolveTypeName(baseName);
                return rank == 1 ? baseType.MakeArrayType() : baseType.MakeArrayType(rank);
            }

            var i = typeName.IndexOf('<');
            if (i >= 0)
            {
                // It's a generic type, definitionType<arg1,arg2,arg3,...>
                var baseName = typeName.Substring(0, i) + "'";
                var typeArgs = new List<Type>();
                i++; // Skip the <
                while (i < typeName.Length - 1)
                {
                    // Get the next type argument, watching for matching angle brackets
                    int n = i;
                    int nestingDepth = 0;
                    while (n < typeName.Length - 1)
                    {
                        if (typeName[n] == '<')
                        {
                            nestingDepth++;
                        }
                        else if (typeName[n] == '>')
                        {
                            if (nestingDepth == 0)
                                break;

                            nestingDepth--;
                        }
                        else if (typeName[n] == ',')
                        {
                            if (nestingDepth == 0)
                                break;
                        }
                        n++;
                    }
                    typeArgs.Add(ResolveTypeName(typeName.Substring(i, n - i)));
                    i = n + 1;
                }
                var baseType = ResolveTypeName(baseName + typeArgs.Count);
                return baseType.MakeGenericType(typeArgs.ToArray<Type>());
            }
            
            throw new TypeAccessException("Type string \"" + typeName + "\" cannot be resolved.");
        }

#endregion
        
        /// <summary>
        /// Internal test method to do a round-trip Serialize+Deserialize loop
        /// </summary>
        public T RoundTripSerializationForTesting<T>(T source)
        {
            byte[] data = this.SerializeToByteArray(source);
            return this.DeserializeFromByteArray<T>(data);
        }

        public void LogRegisteredTypes()
        {
            int count = 0;
            var lines = new StringBuilder();
            foreach (var name in types.Keys.OrderBy(k => k))
            {
                var line = new StringBuilder();
                RuntimeTypeHandle typeHandle = types[name].TypeHandle;
                bool discardLine = true;

                line.Append("    - ");
                line.Append(name);
                line.Append(" :");
                if (copiers.ContainsKey(typeHandle))
                {
                    line.Append(" copier");
                    discardLine = false;
                }
                if (deserializers.ContainsKey(typeHandle))
                {
                    line.Append(" deserializer");
                    discardLine = false;
                }
                if (serializers.ContainsKey(typeHandle))
                {
                    line.Append(" serializer");
                    discardLine = false;
                }
                if (!discardLine)
                {
                    line.AppendLine();
                    lines.Append(line);
                    ++count;
                }
            }

            var report = String.Format("Registered artifacts for {0} types:" + Environment.NewLine + "{1}", count, lines);
            logger.Debug(ErrorCode.SerMgr_ArtifactReport, report);
        }

        /// <summary>
        /// Loads the external srializers and places them into a hash set
        /// </summary>
        /// <param name="providerTypes">The list of types that implement <see cref="IExternalSerializer"/></param>
        private void RegisterSerializationProviders(List<TypeInfo> providerTypes)
        {
            if (providerTypes == null)
            {
                return;
            }

            externalSerializers.Clear();
            typeToExternalSerializerDictionary.Clear();
            providerTypes.ForEach(
                typeInfo =>
                {
                    try
                    {
                        var serializer = ActivatorUtilities.CreateInstance(serviceProvider, typeInfo.AsType()) as IExternalSerializer;
                        externalSerializers.Add(serializer);
                    }
                    catch (Exception exception)
                    {
                        logger.Error(ErrorCode.SerMgr_ErrorLoadingAssemblyTypes, "Failed to create instance of type: " + typeInfo.FullName, exception);
                    }
                });

            if (this.ServiceProvider != null)
            {
                foreach (var serializer in this.ServiceProvider.GetServices<IKeyedSerializer>())
                {
                    this.orderedKeyedSerializers.Add(serializer);
                    this.keyedSerializers[serializer.SerializerId] = serializer;
                }
            }
        }

        public void Dispose()
        {
            IDisposable disposable = this.serializationContext;
            if (disposable != null) disposable.Dispose();
            disposable = this.deserializationContext;
            if (disposable != null) disposable.Dispose();
        }
    }
}
