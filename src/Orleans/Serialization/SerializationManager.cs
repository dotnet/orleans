/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;

namespace Orleans.Serialization
{
    /// <summary>
    /// SerializationManager to oversee the Orleans syrializer system.
    /// </summary>
    public static class SerializationManager
    {
        /// <summary>
        /// Deep copier function.
        /// </summary>
        /// <param name="original">Original object to be deep copied.</param>
        /// <returns>Deep copy of the original object.</returns>
        public delegate object DeepCopier(object original);
        private static readonly Type[] deepCopierParams = { typeof(object) };

        /// <summary> Serializer function. </summary>
        /// <param name="raw">Input object to be serialized.</param>
        /// <param name="stream">Stream to write this data to.</param>
        /// <param name="expected">Current Type active in this stream.</param>
        public delegate void Serializer(object raw, BinaryTokenStreamWriter stream, Type expected);
        private static readonly Type[] serializerParams = { typeof(object), typeof(BinaryTokenStreamWriter), typeof(Type) };

        /// <summary>
        /// Deserializer function.
        /// </summary>
        /// <param name="expected">Expected Type to receive.</param>
        /// <param name="stream">Input stream to be read from.</param>
        /// <returns>Rehydrated object of the specified Type read from the current position in the input stream.</returns>
        public delegate object Deserializer(Type expected, BinaryTokenStreamReader stream);
        private static readonly Type[] deserializerParams = { typeof(Type), typeof(BinaryTokenStreamReader) };

        private static readonly string[] safeFailSerializers = { "Orleans.FSharp" };

        /// <summary>
        /// Toggles whether or not to use the .NET serializer (true) or the Orleans serializer (false).
        /// This is usually set through config.
        /// </summary>
        internal static bool UseStandardSerializer
        {
            get;
            set;
        }

        #region Privates

        private static readonly HashSet<Type> registeredTypes;
        private static readonly HashSet<Assembly> scannedAssemblies;
        private static readonly Dictionary<string, Type> types;
        private static readonly Dictionary<RuntimeTypeHandle, DeepCopier> copiers;
        private static readonly Dictionary<RuntimeTypeHandle, Serializer> serializers;
        private static readonly Dictionary<RuntimeTypeHandle, Deserializer> deserializers;

        private static readonly TraceLogger logger;
        internal static int RegisteredTypesCount { get { return registeredTypes == null ? 0 : registeredTypes.Count; } }

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

        internal static CounterStatistic Copies;
        internal static CounterStatistic Serializations;
        internal static CounterStatistic Deserializations;
        internal static CounterStatistic HeaderSers;
        internal static CounterStatistic HeaderDesers;
        internal static CounterStatistic HeaderSersNumHeaders;
        internal static CounterStatistic HeaderDesersNumHeaders;
        internal static CounterStatistic CopyTimeStatistic;
        internal static CounterStatistic SerTimeStatistic;
        internal static CounterStatistic DeserTimeStatistic;
        internal static CounterStatistic HeaderSerTime;
        internal static CounterStatistic HeaderDeserTime;
        internal static IntValueStatistic TotalTimeInSerializer;

        internal static CounterStatistic FallbackSerializations;
        internal static CounterStatistic FallbackDeserializations;
        internal static CounterStatistic FallbackCopies;
        internal static CounterStatistic FallbackSerTimeStatistic;
        internal static CounterStatistic FallbackDeserTimeStatistic;
        internal static CounterStatistic FallbackCopiesTimeStatistic;

        internal static int LARGE_OBJECT_LIMIT = Constants.LARGE_OBJECT_HEAP_THRESHOLD;

        #endregion

        #region Static initialization

        public static void InitializeForTesting()
        {
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
            // Load serialization info for currently-loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                FindSerializationInfo(assembly);
            }
        }

        internal static void Initialize(bool useStandardSerializer)
        {
            UseStandardSerializer = useStandardSerializer;
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

                TotalTimeInSerializer = IntValueStatistic.FindOrCreate(StatisticNames.SERIALIZATION_TOTAL_TIME_IN_SERIALIZER_MILLIS, () =>
                    {
                        long ticks = CopyTimeStatistic.GetCurrentValue() + SerTimeStatistic.GetCurrentValue() + DeserTimeStatistic.GetCurrentValue()
                                + HeaderSerTime.GetCurrentValue() + HeaderDeserTime.GetCurrentValue();
                        return Utils.TicksToMilliSeconds(ticks);
                    }, CounterStorage.LogAndTable);

                const CounterStorage storeFallback = CounterStorage.LogOnly;
                FallbackSerializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_SERIALIZATION, storeFallback);
                FallbackDeserializations = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DESERIALIZATION, storeFallback);
                FallbackCopies = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DEEPCOPIES, storeFallback);
                FallbackSerTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_SERIALIZATION_MILLIS, storeFallback).AddValueConverter(Utils.TicksToMilliSeconds);
                FallbackDeserTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DESERIALIZATION_MILLIS, storeFallback).AddValueConverter(Utils.TicksToMilliSeconds);
                FallbackCopiesTimeStatistic = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DEEPCOPY_MILLIS, storeFallback).AddValueConverter(Utils.TicksToMilliSeconds);
            }

            InstallAssemblyLoadEventHandler();
        }

        static SerializationManager()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnResolveEventHandler;

            registeredTypes = new HashSet<Type>();
            scannedAssemblies = new HashSet<Assembly>();
            types = new Dictionary<string, Type>();
            copiers = new Dictionary<RuntimeTypeHandle, DeepCopier>();
            serializers = new Dictionary<RuntimeTypeHandle, Serializer>();
            deserializers = new Dictionary<RuntimeTypeHandle, Deserializer>();
            logger = TraceLogger.GetLogger("SerializationManager", TraceLogger.LoggerType.Runtime);
            UseStandardSerializer = false; // Default

            // Built-in handlers: Tuples
            Register(typeof(Tuple<>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            Register(typeof(Tuple<,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            Register(typeof(Tuple<,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            Register(typeof(Tuple<,,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            Register(typeof(Tuple<,,,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);
            Register(typeof(Tuple<,,,,,>), BuiltInTypes.DeepCopyTuple, BuiltInTypes.SerializeTuple, BuiltInTypes.DeserializeTuple);

            // Built-in handlers: enumerables
            Register(typeof(List<>), BuiltInTypes.CopyGenericList, BuiltInTypes.SerializeGenericList, BuiltInTypes.DeserializeGenericList);
            Register(typeof(LinkedList<>), BuiltInTypes.CopyGenericLinkedList, BuiltInTypes.SerializeGenericLinkedList, BuiltInTypes.DeserializeGenericLinkedList);
            Register(typeof(HashSet<>), BuiltInTypes.CopyGenericHashSet, BuiltInTypes.SerializeGenericHashSet, BuiltInTypes.DeserializeGenericHashSet);
            Register(typeof(Stack<>), BuiltInTypes.CopyGenericStack, BuiltInTypes.SerializeGenericStack, BuiltInTypes.DeserializeGenericStack);
            Register(typeof(Queue<>), BuiltInTypes.CopyGenericQueue, BuiltInTypes.SerializeGenericQueue, BuiltInTypes.DeserializeGenericQueue);

            // Built-in handlers: dictionaries
            Register(typeof(Dictionary<,>), BuiltInTypes.CopyGenericDictionary, BuiltInTypes.SerializeGenericDictionary, BuiltInTypes.DeserializeGenericDictionary);
            Register(typeof(Dictionary<string, object>), BuiltInTypes.CopyStringObjectDictionary, BuiltInTypes.SerializeStringObjectDictionary, BuiltInTypes.DeserializeStringObjectDictionary);
            Register(typeof(SortedDictionary<,>), BuiltInTypes.CopyGenericSortedDictionary, BuiltInTypes.SerializeGenericSortedDictionary,
                     BuiltInTypes.DeserializeGenericSortedDictionary);
            Register(typeof(SortedList<,>), BuiltInTypes.CopyGenericSortedList, BuiltInTypes.SerializeGenericSortedList, BuiltInTypes.DeserializeGenericSortedList);

            // Built-in handlers: key-value pairs
            Register(typeof(KeyValuePair<,>), BuiltInTypes.CopyGenericKeyValuePair, BuiltInTypes.SerializeGenericKeyValuePair, BuiltInTypes.DeserializeGenericKeyValuePair);

            // Built-in handlers: nullables
            Register(typeof(Nullable<>), BuiltInTypes.CopyGenericNullable, BuiltInTypes.SerializeGenericNullable, BuiltInTypes.DeserializeGenericNullable);

            // Built-in handlers: Immutables
            Register(typeof(Immutable<>), BuiltInTypes.CopyGenericImmutable, BuiltInTypes.SerializeGenericImmutable, BuiltInTypes.DeserializeGenericImmutable);

            // Built-in handlers: random system types
            Register(typeof(TimeSpan), BuiltInTypes.CopyTimeSpan, BuiltInTypes.SerializeTimeSpan, BuiltInTypes.DeserializeTimeSpan);
            Register(typeof(DateTimeOffset), BuiltInTypes.CopyDateTimeOffset, BuiltInTypes.SerializeDateTimeOffset, BuiltInTypes.DeserializeDateTimeOffset);
            Register(typeof(Type), BuiltInTypes.CopyType, BuiltInTypes.SerializeType, BuiltInTypes.DeserializeType);
            Register(typeof(Guid), BuiltInTypes.CopyGuid, BuiltInTypes.SerializeGuid, BuiltInTypes.DeserializeGuid);
            Register(typeof(IPAddress), BuiltInTypes.CopyIPAddress, BuiltInTypes.SerializeIPAddress, BuiltInTypes.DeserializeIPAddress);
            Register(typeof(IPEndPoint), BuiltInTypes.CopyIPEndPoint, BuiltInTypes.SerializeIPEndPoint, BuiltInTypes.DeserializeIPEndPoint);
            Register(typeof(Uri), BuiltInTypes.CopyUri, BuiltInTypes.SerializeUri, BuiltInTypes.DeserializeUri);

            // Built-in handlers: Orleans internal types
            Register(typeof(InvokeMethodRequest), BuiltInTypes.CopyInvokeMethodRequest, BuiltInTypes.SerializeInvokeMethodRequest,
                     BuiltInTypes.DeserializeInvokeMethodRequest);
            Register(typeof(Response), BuiltInTypes.CopyOrleansResponse, BuiltInTypes.SerializeOrleansResponse,
                     BuiltInTypes.DeserializeOrleansResponse);
            Register(typeof(ActivationId), BuiltInTypes.CopyActivationId, BuiltInTypes.SerializeActivationId, BuiltInTypes.DeserializeActivationId);
            Register(typeof(GrainId), BuiltInTypes.CopyGrainId, BuiltInTypes.SerializeGrainId, BuiltInTypes.DeserializeGrainId);
            Register(typeof(ActivationAddress), BuiltInTypes.CopyActivationAddress, BuiltInTypes.SerializeActivationAddress, BuiltInTypes.DeserializeActivationAddress);
            Register(typeof(CorrelationId), BuiltInTypes.CopyCorrelationId, BuiltInTypes.SerializeCorrelationId, BuiltInTypes.DeserializeCorrelationId);
            Register(typeof(SiloAddress), BuiltInTypes.CopySiloAddress, BuiltInTypes.SerializeSiloAddress, BuiltInTypes.DeserializeSiloAddress);

            // Type names that we need to recognize for generic parameters
            Register(typeof(bool));
            Register(typeof(int));
            Register(typeof(short));
            Register(typeof(sbyte));
            Register(typeof(long));
            Register(typeof(uint));
            Register(typeof(ushort));
            Register(typeof(byte));
            Register(typeof(ulong));
            Register(typeof(float));
            Register(typeof(double));
            Register(typeof(decimal));
            Register(typeof(string));
            Register(typeof(char));
            Register(typeof(DateTime));
            Register(typeof(TimeSpan));
            Register(typeof(object));
            Register(typeof(IPAddress));
            Register(typeof(IPEndPoint));
            Register(typeof(Guid));

            Register(typeof(GrainId));
            Register(typeof(ActivationId));
            Register(typeof(SiloAddress));
            Register(typeof(ActivationAddress));
            Register(typeof(CorrelationId));
            Register(typeof(InvokeMethodRequest));
            Register(typeof(Response));

            Register(typeof(IList<>));
            Register(typeof(IDictionary<,>));
            Register(typeof(IEnumerable<>));

            // Enum names we need to recognize
            Register(typeof(Message.Categories));
            Register(typeof(Message.Directions));
            Register(typeof(Message.LifecycleTag));
            Register(typeof(Message.RejectionTypes));
            Register(typeof(Message.ResponseTypes));
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
        public static void Register(Type t, DeepCopier cop, Serializer ser, Deserializer deser)
        {
            Register(t, cop, ser, deser, false);
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
        public static void Register(Type t, DeepCopier cop, Serializer ser, Deserializer deser, bool forceOverride)
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
                    lock (types)
                    {
                        types[name] = t;
                    }
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

                    if (logger.IsVerbose3) logger.Verbose3("Registered type {0} as {1}", t, name);
                }
            }

            // Register any interfaces this type implements, in order to support passing values that are statically of the interface type
            // but dynamically of this (implementation) type
            foreach (var iface in t.GetInterfaces())
            {
                Register(iface);
            }
            // Do the same for abstract base classes
            var baseType = t.BaseType;
            while (baseType != null)
            {
                if (baseType.IsAbstract)
                    Register(baseType);

                baseType = baseType.BaseType;
            }
        }

        /// <summary>
        /// This method registers a type that has no specific serializer or deserializer.
        /// For instance, abstract base types and interfaces need to be registered this way.
        /// </summary>
        /// <param name="t">Type to be registered.</param>
        public static void Register(Type t)
        {
            string name = t.OrleansTypeKeyString();

            lock (registeredTypes)
            {
                if (registeredTypes.Contains(t))
                {
                    return;
                }

                registeredTypes.Add(t);
                lock (types)
                {
                    types[name] = t;
                }
            }
            if (logger.IsVerbose3) logger.Verbose3("Registered type {0} as {1}", t, name);

            // Register any interfaces this type implements, in order to support passing values that are statically of the interface type
            // but dynamically of this (implementation) type
            foreach (var iface in t.GetInterfaces())
            {
                Register(iface);
            }

            // Do the same for abstract base classes
            var baseType = t.BaseType;
            while (baseType != null)
            {
                if (baseType.IsAbstract)
                    Register(baseType);

                baseType = baseType.BaseType;
            }
        }

        /// <summary>
        /// Looks for types with marked serializer and deserializer methods, and registers them if necessary.
        /// </summary>
        /// <param name="assembly">The assembly to look through.</param>
        internal static void FindSerializationInfo(Assembly assembly)
        {
            // If we're using the .Net serializer, then don't bother with this at all
            if (UseStandardSerializer) return;

            // serialization of reflection-only types isn't supported.
            if (assembly.ReflectionOnly) return;

            // Don't bother re-processing an assembly we've already scanned
            lock (scannedAssemblies)
            {
                if (scannedAssemblies.Contains(assembly)) return;

                scannedAssemblies.Add(assembly);
            }

            bool systemAssembly = 
                !assembly.IsDynamic 
                && (assembly.FullName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase)
                    || assembly.FullName.StartsWith("System.", StringComparison.Ordinal));

            if (logger.IsVerbose2) logger.Verbose2("Scanning assembly {0} for serialization info", assembly.GetLocationSafe());

            try
            {
                // Check each type in the assembly for serializer and deserializer methods
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsEnum)
                    {
                        Register(type);
                    }
                    else if (!systemAssembly)
                    {
                        if (!type.IsInterface && !type.IsAbstract &&
                            (type.Namespace == null ||
                             (!type.Namespace.Equals("System", StringComparison.Ordinal) 
                                && !type.Namespace.StartsWith("System.", StringComparison.Ordinal))))
                        {
                            if (type.GetCustomAttributes(typeof(RegisterSerializerAttribute), false).Length > 0)
                            {
                                // Call the static Register method on the type
                                if (logger.IsVerbose3) logger.Verbose3("Running register method for type {0} from assembly {1}",
                                    type.Name, assembly.GetName().Name);

                                var register = type.GetMethod("Register");
                                if (register != null)
                                {
                                    try
                                    {
                                        register.Invoke(null, Type.EmptyTypes);
                                    }
                                    catch (OrleansException ex)
                                    {
                                        logger.Error(ErrorCode.SerMgr_TypeRegistrationFailure, "Failure registering type " + type.OrleansTypeName() + " from assembly " + assembly.GetLocationSafe(), ex);
                                        throw;
                                    }
                                    catch(Exception)
                                    {
                                        // Ignore failures to load our own serializers, such as the F# ones in case F# isn't installed.
                                        if (safeFailSerializers.Contains(assembly.GetName().Name))
                                            logger.Warn(ErrorCode.SerMgr_TypeRegistrationFailureIgnore, "Failure registering type " + type.OrleansTypeName() + " from assembly " + assembly.GetLocationSafe() + ". Ignoring it.");
                                        else
                                            throw;
                                    }
                                }
                                else
                                {
                                    logger.Warn(ErrorCode.SerMgr_MissingRegisterMethod,
                                        "Type {0} from assembly {1} has the RegisterSerializer attribute but no Register static method",
                                        type.Name, assembly.GetName().Name);
                                }
                            }
                            else
                            {
                                MethodInfo copier = null;
                                MethodInfo serializer = null;
                                MethodInfo deserializer = null;
                                foreach ( var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                                {
                                    if (method.GetCustomAttributes(typeof(CopierMethodAttribute), true).Length > 0)
                                    {
                                        copier = method;
                                    }
                                    else if (method.GetCustomAttributes(typeof(SerializerMethodAttribute), true).Length > 0)
                                    {
                                        serializer = method;
                                    }
                                    else if (method.GetCustomAttributes(typeof(DeserializerMethodAttribute), true).Length > 0)
                                    {
                                        deserializer = method;
                                    }
                                }
                                if ((serializer != null) && (deserializer != null) && (copier != null))
                                {
                                    try
                                    {
                                        if (type.IsGenericTypeDefinition)
                                        {
                                            Register(type,
                                                     obj =>
                                                     {
                                                         var t = obj.GetType();
                                                         var concreteCop = t.GetMethod(copier.Name, deepCopierParams);
                                                         var cop = (DeepCopier)concreteCop.CreateDelegate(typeof(DeepCopier));
                                                         var concreteSer = t.GetMethod(serializer.Name, serializerParams);
                                                         var ser = (Serializer)concreteSer.CreateDelegate(typeof(Serializer));
                                                         var concreteDeser = t.GetMethod(deserializer.Name, deserializerParams);
                                                         var deser = (Deserializer)concreteDeser.CreateDelegate(typeof(Deserializer));
                                                         Register(obj.GetType(), cop, ser, deser, true);
                                                         return cop(obj);
                                                     },
                                                     (obj, stream, exp) =>
                                                     {
                                                         var t = obj.GetType();
                                                         var concreteCop = t.GetMethod(copier.Name, deepCopierParams);
                                                         var cop = (DeepCopier)concreteCop.CreateDelegate(typeof(DeepCopier));
                                                         var concreteSer = t.GetMethod(serializer.Name, serializerParams);
                                                         var ser = (Serializer)concreteSer.CreateDelegate(typeof(Serializer));
                                                         var concreteDeser = t.GetMethod(deserializer.Name, deserializerParams);
                                                         var deser = (Deserializer)concreteDeser.CreateDelegate(typeof(Deserializer));
                                                         Register(obj.GetType(), cop, ser, deser, true);
                                                         ser(obj, stream, exp);
                                                     },
                                                     (t, stream) =>
                                                     {
                                                         var concreteCop = t.GetMethod(copier.Name, deepCopierParams);
                                                         var cop = (DeepCopier)concreteCop.CreateDelegate(typeof(DeepCopier));
                                                         var concreteSer = t.GetMethod(serializer.Name, serializerParams);
                                                         var ser = (Serializer)concreteSer.CreateDelegate(typeof(Serializer));
                                                         var concreteDeser = t.GetMethod(deserializer.Name, deserializerParams);
                                                         var deser = (Deserializer)concreteDeser.CreateDelegate(typeof(Deserializer));
                                                         Register(t, cop, ser, deser, true);
                                                         return deser(t, stream);
                                                     }, true);
                                        }
                                        else
                                        {
                                            Register(type,
                                                (DeepCopier)copier.CreateDelegate(typeof(DeepCopier)),
                                                (Serializer)serializer.CreateDelegate(typeof(Serializer)),
                                                (Deserializer)deserializer.CreateDelegate(typeof(Deserializer)), true);
                                        }
                                    }
                                    catch (ArgumentException)
                                    {
                                        logger.Warn(ErrorCode.SerMgr_ErrorBindingMethods, "Error binding serialization methods for type {0}", type.OrleansTypeName());
                                        throw;
                                    }
                                    if (logger.IsVerbose3) logger.Verbose3("Loaded serialization info for type {0} from assembly {1}", type.Name, assembly.GetName().Name);
                                }
                                else if ((serializer != null) && (deserializer != null))
                                {
                                    try
                                    {
                                        Register(type, null,
                                            (Serializer)serializer.CreateDelegate(typeof(Serializer)),
                                            (Deserializer)deserializer.CreateDelegate(typeof(Deserializer)), true);
                                    }
                                    catch (ArgumentException)
                                    {
                                        logger.Warn(ErrorCode.SerMgr_ErrorBindingMethods, "Error binding serialization methods for type {0}", type.OrleansTypeName());
                                        throw;
                                    }
                                    if (logger.IsVerbose3) logger.Verbose3("Loaded serialization info for type {0} from assembly {1}", type.Name, assembly.GetName().Name);
                                }
                                else if (copier != null)
                                {
                                    try
                                    {
                                        Register(type, (DeepCopier)copier.CreateDelegate(typeof(DeepCopier)), null, null, true);
                                    }
                                    catch (ArgumentException)
                                    {
                                        logger.Warn(ErrorCode.SerMgr_ErrorBindingMethods, "Error binding serialization methods for type {0}", type.OrleansTypeName());
                                        throw;
                                    }
                                    if (logger.IsVerbose3) logger.Verbose3("Loaded serialization info for type {0} from assembly {1}", type.Name, assembly.GetName().Name);
                                }
                                else if (!type.IsSerializable)
                                {
                                    // Comparers with no fields can be safely dealt with as just a type name
                                    var comparer = false;
                                    foreach (var iface in type.GetInterfaces())
                                        if (iface.IsGenericType &&
                                            (iface.GetGenericTypeDefinition() == typeof (IComparer<>)
                                             || iface.GetGenericTypeDefinition() == typeof (IEqualityComparer<>)))
                                        {
                                            comparer = true;
                                            break;
                                        }

                                    if (comparer && (type.GetFields().Length == 0))
                                        Register(type);
                                }
                                else
                                {
                                    Register(type);
                                }
                            }
                        }
                        else
                        {
                            Register(type);
                        }
                    }
                }
            }
            catch (ReflectionTypeLoadException rtle)
            {
                var sb = new StringBuilder();
                foreach (var ex in rtle.LoaderExceptions)
                    if (ex != null)
                        sb.AppendFormat("    Exception loading type: {0}", ex).AppendLine();

                foreach (var t in rtle.Types)
                    if (t != null)
                        sb.AppendFormat("    Successfully loaded type {0}", t.Name).AppendLine();

                logger.Warn(ErrorCode.SerMgr_ErrorLoadingAssemblyTypes,
                    "Error loading types for assembly {0}: {1}", assembly.GetName().Name, sb.ToString());
            }
        }

        #endregion

        #region Deep copying

        internal static DeepCopier GetCopier(Type t)
        {
            lock (copiers)
            {
                DeepCopier copier;
                if (copiers.TryGetValue(t.TypeHandle, out copier))
                    return copier;

                if (!t.IsGenericType) return null;

                if (copiers.TryGetValue(t.GetGenericTypeDefinition().TypeHandle, out copier))
                    return copier;
            }

            return null;
        }

        /// <summary>
        /// Deep copy the specified object, using DeepCopier functions previously registered for this type.
        /// </summary>
        /// <param name="original">The input data to be deep copied.</param>
        /// <returns>Deep copied clone of the original input object.</returns>
        public static object DeepCopy(object original)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                Copies.Increment();
            }

            SerializationContext.Current.Reset();
            object copy = DeepCopyInner(original);
            SerializationContext.Current.Reset();
            

            if (timer!=null)
            {
                timer.Stop();
                CopyTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
            
            return copy;
        }

        /// <summary>
        /// <para>
        /// This method makes a deep copy of the object passed to it.
        /// </para>
        /// </summary>
        /// <param name="original">The input data to be deep copied.</param>
        /// <returns>Deep copied clone of the original input object.</returns>
        public static object DeepCopyInner(object original)
        {
            if (original == null) return null;

            var t = original.GetType();
            var shallow = t.IsOrleansShallowCopyable();

            if (shallow)
                return original;

            var reference = SerializationContext.Current.CheckObjectWhileCopying(original);
            if (reference != null)
                return reference;

            object copy;

            var copier = GetCopier(t);
            if (copier != null)
            {
                copy = copier(original);
                SerializationContext.Current.RecordObject(original, copy);
            }
            else
            {
                copy = DeepCopierHelper(t, original);
            }

            return copy;
        }

        private static object DeepCopierHelper(Type t, object original)
        {
            // Arrays are all that's left. 
            // Handling arbitrary-rank arrays is a bit complex, but why not?
            var originalArray = original as Array;
            if (originalArray != null)
            {
                if (originalArray.Rank == 1 && originalArray.GetLength(0) == 0)
                {
                    // A common special case - empty one dimentional array
                    return originalArray;
                }
                // A common special case
                if ((original is byte[]) && (originalArray.Rank == 1))
                {
                    var source = (byte[])original;
                    if (source.Length > LARGE_OBJECT_LIMIT)
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
                if (et.IsOrleansShallowCopyable())
                {
                    // Only check the size for primitive types because otherwise Buffer.ByteLength throws
                    if (et.IsPrimitive && Buffer.ByteLength(originalArray) > LARGE_OBJECT_LIMIT)
                    {
                        logger.Info(ErrorCode.Ser_LargeObjectAllocated,
                            "Large {0} array of total byte size {1} is being copied. This will result in an allocation on the large object heap. " +
                            "Frequent allocations to the large object heap can result in frequent gen2 garbage collections and poor system performance. " +
                            "Please consider using Immutable<{0}> instead.", t.OrleansTypeName(), Buffer.ByteLength(originalArray));
                    }
                    return originalArray.Clone();
                }

                // We assume that all arrays have lower bound 0. In .NET 4.0, it's hard to create an array with a non-zero lower bound.
                var rank = originalArray.Rank;
                var lengths = new int[rank];
                for (var i = 0; i < rank; i++)
                    lengths[i] = originalArray.GetLength(i);

                var copyArray = Array.CreateInstance(et, lengths);
                SerializationContext.Current.RecordObject(original, copyArray);

                if (rank == 1)
                {
                    for (var i = 0; i < lengths[0]; i++)
                        copyArray.SetValue(DeepCopyInner(originalArray.GetValue(i)), i);
                }
                else if (rank == 2)
                {
                    for (var i = 0; i < lengths[0]; i++)
                        for (var j = 0; j < lengths[1]; j++)
                            copyArray.SetValue(DeepCopyInner(originalArray.GetValue(i, j)), i, j);
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
                        copyArray.SetValue(DeepCopyInner(originalArray.GetValue(index)), index);
                    }
                }
                return copyArray;

            }

            if (t.IsSerializable)
                return FallbackSerializationDeepCopy(original);

            throw new OrleansException("No copier found for object of type " + t.OrleansTypeName() + 
                ". Perhaps you need to mark it [Serializable] or define a custom serializer for it?");
        }

        #endregion

        #region Serializing

        internal static Serializer GetSerializer(Type t)
        {
            lock (serializers)
            {
                Serializer ser;
                if (serializers.TryGetValue(t.TypeHandle, out ser))
                    return ser;

                if (t.IsGenericType)
                    if (serializers.TryGetValue(t.GetGenericTypeDefinition().TypeHandle, out ser))
                        return ser;
            }

            return null;
        }

        /// <summary>
        /// Serialize the specified object, using Serializer functions previously registered for this type.
        /// </summary>
        /// <param name="raw">The input data to be serialized.</param>
        /// <param name="stream">The output stream to write to.</param>
        public static void Serialize(object raw, BinaryTokenStreamWriter stream)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                Serializations.Increment();
            }
            
            SerializationContext.Current.Reset();
            SerializeInner(raw, stream, null);
            SerializationContext.Current.Reset();
            
            if (timer!=null)
            {
                timer.Stop();
                SerTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
        }

        /// <summary>
        /// Encodes the object to the provided binary token stream.
        /// </summary>
        /// <param name="raw">The input data to be serialized.</param>
        /// <param name="stream">The output stream to write to.</param>
        /// <param name="expected">Current expected Type on this stream.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2201:DoNotRaiseReservedExceptionTypes")]
        public static void SerializeInner(object obj, BinaryTokenStreamWriter stream, Type expected)
        {
            // Nulls get special handling
            if (obj == null)
            {
                stream.WriteNull();
                return;
            }

            var t = obj.GetType();
            // Enums are extra-special
            if (t.IsEnum)
            {
                stream.WriteTypeHeader(t, expected);
                stream.Write(Convert.ToInt32(obj));
                return;
            }

            // Check for simple types
            if (stream.TryWriteSimpleObject(obj)) return;

            // Check for primitives
            // At this point, we're either an object or a non-trivial value type

            // Start by checking to see if we're a back-reference, and recording us for possible future back-references if not
            if (!t.IsValueType)
            {
                int reference = SerializationContext.Current.CheckObjectWhileSerializing(obj);
                if (reference >= 0)
                {
                    stream.WriteReference(reference);
                    return;
                }
                SerializationContext.Current.RecordObject(obj, stream.CurrentOffset);
            }

            // If we're simply a plain old unadorned, undifferentiated object, life is easy
            if (t.TypeHandle.Equals(objectTypeHandle))
            {
                stream.Write(SerializationTokenType.SpecifiedType);
                stream.Write(SerializationTokenType.Object);
                return;
            }

            // Arrays get handled specially
            if (t.IsArray)
            {
                var et = t.GetElementType();
                if (HasOrleansSerialization(et))
                {
                    SerializeArray((Array)obj, stream, expected, et);
                }
                else if (et.IsSerializable)
                {
                    FallbackSerializer(obj, stream);
                }
                else
                {
                    FallbackSerializer(obj, stream);
                }
                return;
            }

            Serializer ser = GetSerializer(t);
            if (ser != null)
            {
                stream.WriteTypeHeader(t, expected);
                ser(obj, stream, expected);
                return;
            }

            if (t.IsSerializable)
            {
                FallbackSerializer(obj, stream);
                return;
            }

            if ((obj is Exception) && !t.IsSerializable)
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
                FallbackSerializer(foo, stream);
                return;
            }

            throw new ArgumentException("No serializer found for object of type " + t.OrleansTypeName()
                 + ". Perhaps you need to mark it [Serializable] or define a custom serializer for it?");
        }

        // We assume that all lower bounds are 0, since creating an array with lower bound !=0 is hard in .NET 4.0+
        private static void SerializeArray(Array array, BinaryTokenStreamWriter stream, Type expected, Type et)
        {
            // First check for one of the optimized cases
            if (array.Rank == 1)
            {
                if (et.TypeHandle.Equals(byteTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.ByteArray);
                    stream.Write(array.Length);
                    stream.Write((byte[])array);
                    return;
                }
                if (et.TypeHandle.Equals(boolTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.BoolArray);
                    stream.Write(array.Length);
                    stream.Write((bool[])array);
                    return;
                }
                if (et.TypeHandle.Equals(charTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.CharArray);
                    stream.Write(array.Length);
                    stream.Write((char[])array);
                    return;
                }
                if (et.TypeHandle.Equals(shortTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.ShortArray);
                    stream.Write(array.Length);
                    stream.Write((short[])array);
                    return;
                }
                if (et.TypeHandle.Equals(intTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.IntArray);
                    stream.Write(array.Length);
                    stream.Write((int[])array);
                    return;
                }
                if (et.TypeHandle.Equals(longTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.LongArray);
                    stream.Write(array.Length);
                    stream.Write((long[])array);
                    return;
                }
                if (et.TypeHandle.Equals(ushortTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.UShortArray);
                    stream.Write(array.Length);
                    stream.Write((ushort[])array);
                    return;
                }
                if (et.TypeHandle.Equals(uintTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.UIntArray);
                    stream.Write(array.Length);
                    stream.Write((uint[])array);
                    return;
                }
                if (et.TypeHandle.Equals(ulongTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.ULongArray);
                    stream.Write(array.Length);
                    stream.Write((ulong[])array);
                    return;
                }
                if (et.TypeHandle.Equals(floatTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.FloatArray);
                    stream.Write(array.Length);
                    stream.Write((float[])array);
                    return;
                }
                if (et.TypeHandle.Equals(doubleTypeHandle))
                {
                    stream.Write(SerializationTokenType.SpecifiedType);
                    stream.Write(SerializationTokenType.DoubleArray);
                    stream.Write(array.Length);
                    stream.Write((double[])array);
                    return;
                }
            }

            // Write the array header
            stream.WriteArrayHeader(array, expected);

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
                    SerializeInner(array.GetValue(i), stream, et);
            }
            else if (rank == 2)
            {
                for (int i = 0; i < lengths[0]; i++)
                    for (int j = 0; j < lengths[1]; j++)
                        SerializeInner(array.GetValue(i, j), stream, et);
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
                    SerializeInner(array.GetValue(index), stream, et);
                }
            }
        }

        /// <summary>
        /// Serialize data into byte[].
        /// </summary>
        /// <param name="raw">Input data.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public static byte[] SerializeToByteArray(object raw)
        {
            var stream = new BinaryTokenStreamWriter();
            byte[] result;
            try
            {
                SerializationContext.Current.Reset();
                SerializeInner(raw, stream, null);
                SerializationContext.Current.Reset();
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
        public static object Deserialize(BinaryTokenStreamReader stream)
        {
            return Deserialize(null, stream);
        }

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <typeparam name="T">Type to return.</typeparam>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public static T Deserialize<T>(BinaryTokenStreamReader stream)
        {
            return (T)Deserialize(typeof(T), stream);
        }

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <param name="t">Type to return.</param>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public static object Deserialize(Type t, BinaryTokenStreamReader stream)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                Deserializations.Increment();
            }
            object result = null;
            
            DeserializationContext.Current.Reset();
            result = DeserializeInner(t, stream);
            DeserializationContext.Current.Reset();
            
            if (timer!=null)
            {
                timer.Stop();
                DeserTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
            return result;
        }

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <typeparam name="T">Type to return.</typeparam>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public static T DeserializeInner<T>(BinaryTokenStreamReader stream)
        {
            return (T)DeserializeInner(typeof(T), stream);
        }

        /// <summary>
        /// Deserialize the next object from the input binary stream.
        /// </summary>
        /// <param name="expected">Type to return.</param>
        /// <param name="stream">Input stream.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public static object DeserializeInner(Type expected, BinaryTokenStreamReader stream)
        {
            var start = stream.CurrentPosition;

            // NOTE: we don't check that the actual dynamic result implements the expected type. We'll allow a cast exception higher up to catch this.

            SerializationTokenType token;
            object result;
            if (stream.TryReadSimpleType(out result, out token))
                return result;

            // Special serializations (reference, fallback)
            if (token == SerializationTokenType.Reference)
            {
                var offset = stream.ReadInt();
                return DeserializationContext.Current.FetchReferencedObject(offset);
            }
            if (token == SerializationTokenType.Fallback)
            {
                var fallbackResult = FallbackDeserializer(stream);
                DeserializationContext.Current.RecordObject(start, fallbackResult);
                return fallbackResult;
            }

            Type resultType;
            if (token == SerializationTokenType.ExpectedType)
            {
                if (expected == null)
                    throw new SerializationException("ExpectedType token encountered but no expected type provided");

                resultType = expected;
            }
            else if (token == SerializationTokenType.SpecifiedType)
            {
                resultType = stream.ReadSpecifiedTypeHeader();
            }
            else
            {
                throw new SerializationException("Unexpected token '" + token + "' introducing type specifier");
            }

            // Handle object, which is easy
            if (resultType.TypeHandle.Equals(objectTypeHandle))
                return new object();

            // Handle enums
            if (resultType.IsEnum)
            {
                result = Enum.ToObject(resultType, stream.ReadInt());
                return result;
            }

            if (resultType.IsArray)
            {
                result = DeserializeArray(resultType, stream);
                DeserializationContext.Current.RecordObject(start, result);
                return result;
            }

            var deser = GetDeserializer(resultType);
            if (deser != null)
            {
                result = deser(resultType, stream);
                DeserializationContext.Current.RecordObject(start, result);
                return result;
            }

            throw new SerializationException("Unsupported type '" + resultType.OrleansTypeName() + 
                "' encountered. Perhaps you need to mark it [Serializable] or define a custom serializer for it?");
        }

        private static object DeserializeArray(Type resultType, BinaryTokenStreamReader stream)
        {
            var lengths = ReadArrayLengths(resultType.GetArrayRank(), stream);
            var rank = lengths.Length;
            var et = resultType.GetElementType();

            // Optimized special cases
            if (rank == 1)
            {
                if (et.TypeHandle.Equals(byteTypeHandle))
                    return stream.ReadBytes(lengths[0]);

                if (et.TypeHandle.Equals(sbyteTypeHandle))
                {
                    var result = new sbyte[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(shortTypeHandle))
                {
                    var result = new short[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(intTypeHandle))
                {
                    var result = new int[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(longTypeHandle))
                {
                    var result = new long[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(ushortTypeHandle))
                {
                    var result = new ushort[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(uintTypeHandle))
                {
                    var result = new uint[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(ulongTypeHandle))
                {
                    var result = new ulong[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(doubleTypeHandle))
                {
                    var result = new double[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(floatTypeHandle))
                {
                    var result = new float[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(charTypeHandle))
                {
                    var result = new char[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
                if (et.TypeHandle.Equals(boolTypeHandle))
                {
                    var result = new bool[lengths[0]];
                    var n = Buffer.ByteLength(result);
                    stream.ReadBlockInto(result, n);
                    return result;
                }
            }

            var array = Array.CreateInstance(et, lengths);

            if (rank == 1)
            {
                for (int i = 0; i < lengths[0]; i++)
                    array.SetValue(DeserializeInner(et, stream), i);
            }
            else if (rank == 2)
            {
                for (int i = 0; i < lengths[0]; i++)
                    for (int j = 0; j < lengths[1]; j++)
                        array.SetValue(DeserializeInner(et, stream), i, j);
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
                    array.SetValue(DeserializeInner(et, stream), index);
                }
            }

            return array;
        }

        private static int[] ReadArrayLengths(int n, BinaryTokenStreamReader stream)
        {
            var result = new int[n];
            for (var i = 0; i < n; i++)
                result[i] = stream.ReadInt();

            return result;
        }

        internal static Deserializer GetDeserializer(Type t)
        {
            Deserializer deser;
            bool found;

            lock (deserializers)
            {
                found = deserializers.TryGetValue(t.TypeHandle, out deser);
            }
            if (found)
                return deser;

            if (t.IsGenericType)
            {
                lock (deserializers)
                {
                    found = deserializers.TryGetValue(t.GetGenericTypeDefinition().TypeHandle, out deser);
                }
                if (found)
                    return deser;
            }

            return null;
        }

        /// <summary>
        /// Deserialize data from the specified byte[] and rehydrate backi into objects.
        /// </summary>
        /// <typeparam name="T">Type of data to be returned.</typeparam>
        /// <param name="data">Input data.</param>
        /// <returns>Object of the required Type, rehydrated from the input stream.</returns>
        public static T DeserializeFromByteArray<T>(byte[] data)
        {
            var stream = new BinaryTokenStreamReader(data);
            DeserializationContext.Current.Reset();
            var result = DeserializeInner<T>(stream);
            DeserializationContext.Current.Reset();
            return result;
        }

        #endregion

        #region Special case code for message headers

        internal static void SerializeMessageHeaders(Dictionary<Message.Header, object> headers, BinaryTokenStreamWriter stream)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
            }
            SerializeMessageHeaderDictHelper(headers, stream);

            if (timer != null)
            {
                timer.Stop();
                HeaderSers.Increment();
                HeaderSersNumHeaders.IncrementBy(headers.Count);
                HeaderSerTime.IncrementBy(timer.ElapsedTicks);
            }
        }

        private static void SerializeMessageHeaderDictHelper(Dictionary<Message.Header, object> headers, BinaryTokenStreamWriter stream)
        {
            stream.Write(SerializationTokenType.StringObjDict);
            stream.Write(headers.Count);
            foreach (var header in headers)
            {
                stream.Write((byte)header.Key);
                SerializeMessageHeaderValueHelper(header.Value, stream);
            }
        }

        private static void SerializeMessageHeaderListHelper(List<object> list, BinaryTokenStreamWriter stream)
        {
            stream.Write(SerializationTokenType.ObjList);
            stream.Write(list.Count);
            foreach (var item in list)
                SerializeMessageHeaderValueHelper(item, stream);
        }

        private static void SerializeMessageHeaderValueHelper(object value, BinaryTokenStreamWriter stream)
        {
            if (value == null)
            {
                stream.WriteNull();
                return;
            }

            if (value is Enum)
            {
                // Within message headers, enums get serialized as integers, and get re-cast in the code
                stream.Write(SerializationTokenType.Int);
                stream.Write(Convert.ToInt32(value));
                return;
            }

            if (stream.TryWriteSimpleObject(value))
                return;

            if (value is Dictionary<Message.Header, object>)
            {
                SerializeMessageHeaderDictHelper((Dictionary<Message.Header, object>)value, stream);
                return;
            }

            if (value is List<object>)
            {
                SerializeMessageHeaderListHelper((List<object>)value, stream);
                return;
            }

            var t = value.GetType();
            var ser = GetSerializer(t);
            if (ser != null)
            {
                stream.WriteTypeHeader(t);
                ser(value, stream, null);
                return;
            }

            throw new ArgumentException("Invalid message header passed to SerializeMessageHeaders; type is " + value.GetType().Name, "value");
        }

        internal static Dictionary<Message.Header, object> DeserializeMessageHeaders(BinaryTokenStreamReader stream)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
            }
            var token = stream.ReadToken();
            if (token != SerializationTokenType.StringObjDict)
            {
                if (token == SerializationTokenType.SpecifiedType)
                {
                    Type t = null;
                    try
                    {
                        t = stream.ReadSpecifiedTypeHeader();
                    }
                    catch (Exception) { }

                    if(t != null)
                        throw new SerializationException(string.Format("Introductory token for message headers is incorrect: token = {0}, SpecifiedTypeHeader = {1} ", token, t));
                }
                throw new SerializationException(string.Format("Introductory token for message headers is incorrect: {0}", token));
            }
            var result = DeserializeMessageHeaderDictHelper(stream);

            if (timer != null)
            {
                timer.Stop();
                HeaderDesers.Increment();
                HeaderDesersNumHeaders.IncrementBy(result.Count);
                HeaderDeserTime.IncrementBy(timer.ElapsedTicks);
            }
            return result;
        }

        private static Dictionary<Message.Header, object> DeserializeMessageHeaderDictHelper(BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var result = new Dictionary<Message.Header, object>(count);
            for (var i = 0; i < count; i++)
            {
                var key = stream.ReadByte();
                result.Add((Message.Header)key, DeserializeMessageHeaderHelper(stream));
            }
            return result;
        }

        private static List<object> DeserializeMessageHeaderListHelper(BinaryTokenStreamReader stream)
        {
            var count = stream.ReadInt();
            var result = new List<object>(count);
            for (var i = 0; i < count; i++)
                result.Add(DeserializeMessageHeaderHelper(stream));

            return result;
        }


        private static object DeserializeMessageHeaderHelper(BinaryTokenStreamReader stream)
        {
            object result;
            SerializationTokenType token;
            if (stream.TryReadSimpleType(out result, out token))
                return result;

            if (token == SerializationTokenType.ObjList)
                return DeserializeMessageHeaderListHelper(stream);

            if (token == SerializationTokenType.StringObjDict)
                return DeserializeMessageHeaderDictHelper(stream);

            if (token == SerializationTokenType.SpecifiedType)
            {
                var t = stream.ReadSpecifiedTypeHeader();
                var des = GetDeserializer(t);
                if (des != null)
                    return des(t, stream);
            }
            throw new SerializationException(string.Format("Unexpected token {0} parsing message headers", token));
        }

        #endregion

        #region Fallback serializer and deserializer

        private static void FallbackSerializer(object raw, BinaryTokenStreamWriter stream)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                FallbackSerializations.Increment();
            }

            var formatter = new BinaryFormatter();
            byte[] bytes;
            using (var memoryStream = new MemoryStream())
            {
                formatter.Serialize(memoryStream, raw);
                memoryStream.Flush();
                bytes = memoryStream.ToArray();
            }
            stream.Write(SerializationTokenType.Fallback);
            stream.Write(bytes.Length);
            stream.Write(bytes);

            if (StatisticsCollector.CollectSerializationStats)
            {
                timer.Stop();
                FallbackSerTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
        }

        private static object FallbackDeserializer(BinaryTokenStreamReader stream)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                FallbackDeserializations.Increment();
            }

            var n = stream.ReadInt();
            var bytes = stream.ReadBytes(n);
            var formatter = new BinaryFormatter();
            object ret = null;
            using (var memoryStream = new MemoryStream(bytes))
            {
                ret = formatter.Deserialize(memoryStream);
            }

            if (timer != null)
            {
                timer.Stop();
                FallbackDeserTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
            return ret;
        }

        private static Assembly OnResolveEventHandler(Object sender, ResolveEventArgs arg)
        {
            // types defined in assemblies loaded by path name (e.g. Assembly.LoadFrom) aren't resolved during deserialization without some help.
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
                if (assembly.FullName == arg.Name)
                    return assembly;

            return null;
        }

        private static object FallbackSerializationDeepCopy(object obj)
        {
            Stopwatch timer = null;
            if (StatisticsCollector.CollectSerializationStats)
            {
                timer = new Stopwatch();
                timer.Start();
                FallbackCopies.Increment();
            }

            var formatter = new BinaryFormatter();
            object ret = null;
            using (var memoryStream = new MemoryStream())
            {
                formatter.Serialize(memoryStream, obj);
                memoryStream.Flush();
                memoryStream.Seek(0, SeekOrigin.Begin);
                formatter.Binder = DynamicBinder.Instance;
                ret = formatter.Deserialize(memoryStream);
            }

            if (StatisticsCollector.CollectSerializationStats)
            {
                timer.Stop();
                FallbackCopiesTimeStatistic.IncrementBy(timer.ElapsedTicks);
            }
            return ret;
        }

        #endregion

        #region Utilities

        private static bool HasOrleansSerialization(Type t)
        {
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean:
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                case TypeCode.Char:
                case TypeCode.DateTime:
                    return true;
                default:
                    if (t.IsArray)
                        return HasOrleansSerialization(t.GetElementType());

                    return t == typeof(string) || serializers.ContainsKey(t.TypeHandle);
            }
        }

        internal static Type ResolveTypeName(string typeName)
        {
            Type t;

            if (types.TryGetValue(typeName, out t))
                return t;

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
        public static T RoundTripSerializationForTesting<T>(T source)
        {
            byte[] data = SerializeToByteArray(source);
            return DeserializeFromByteArray<T>(data);
        }

        private static void InstallAssemblyLoadEventHandler()
        {
            // initialize serialization for all assemblies to be loaded.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            // initialize serialization for already loaded assemblies.
            foreach (var assembly in assemblies)
                FindSerializationInfo(assembly);
        }

        public static void LogRegisteredTypes()
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
                if (serializers.ContainsKey(typeHandle))
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

            var report = string.Format("Registered artifacts for {0} types:" + Environment.NewLine + "{1}", count, lines);
            logger.LogWithoutBulkingAndTruncating(Logger.Severity.Verbose, ErrorCode.SerMgr_ArtifactReport, report);
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            FindSerializationInfo(args.LoadedAssembly);
        }

        /// <summary>
        /// This appears necessary because the BinaryFormatter by default will not see types
        /// that are defined by the InvokerGenerator.
        /// Needs to be public since it used by generated client code.
        /// </summary>
        class DynamicBinder : SerializationBinder
        {
            public static readonly SerializationBinder Instance = new DynamicBinder();

            private readonly Dictionary<string, Assembly> assemblies = new Dictionary<string, Assembly>();

            public override Type BindToType(string assemblyName, string typeName)
            {
                lock (assemblies)
                {
                    Assembly result;
                    if (!assemblies.TryGetValue(assemblyName, out result))
                    {
                        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                            assemblies[assembly.GetName().FullName] = assembly;

                        // in some cases we have to explicitly load the assembly even though it seems to be already loaded but for some reason it's not listed in AppDomain.CurrentDomain.GetAssemblies()
                        if (!assemblies.TryGetValue(assemblyName, out result))
                            assemblies[assemblyName] = Assembly.Load(assemblyName);

                        result = assemblies[assemblyName];
                    }
                    return result.GetType(typeName);
                }
            }
        }
    }
}
