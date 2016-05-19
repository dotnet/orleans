using System;
using System.Collections.Generic;
using System.Linq;

using Orleans.CodeGeneration;
using Orleans.GrainDirectory;
using Orleans.Runtime.Providers;
using Orleans.Serialization;
using System.Reflection;

namespace Orleans.Runtime
{
    internal class GrainTypeManager
    {
        private IDictionary<string, GrainTypeData> grainTypes;
        private readonly IGrainFactory grainFactory;
        private readonly TraceLogger logger = TraceLogger.GetLogger("GrainTypeManager");
        private readonly GrainInterfaceMap grainInterfaceMap;
        private readonly Dictionary<int, InvokerData> invokers = new Dictionary<int, InvokerData>();
        private readonly SiloAssemblyLoader loader;
        private static readonly object lockable = new object();

        public static GrainTypeManager Instance { get; private set; }

        public IEnumerable<KeyValuePair<string, GrainTypeData>> GrainClassTypeData { get { return grainTypes; } }

        public static void Stop()
        {
            Instance = null;
        }

        public GrainTypeManager(bool localTestMode, IGrainFactory grainFactory, SiloAssemblyLoader loader)
        {
            this.grainFactory = grainFactory;
            this.loader = loader;
            grainInterfaceMap = new GrainInterfaceMap(localTestMode);
            lock (lockable)
            {
                if (Instance != null)
                    throw new InvalidOperationException("An attempt to create a second insance of GrainTypeManager.");
                Instance = this;
            }
        }

        public void Start(bool strict = true)
        {
            // loading application assemblies now occurs in four phases.
            // 1. We scan the file system for assemblies meeting pre-determined criteria, specified in SiloAssemblyLoader.LoadApplicationAssemblies (called by the constructor).
            // 2. We load those assemblies into memory. In the official distribution of Orleans, this is usually 4 assemblies.

            // Generate code for newly loaded assemblies.
            CodeGeneratorManager.GenerateAndCacheCodeForAllAssemblies();

            // (no more assemblies should be loaded into memory, so now is a good time to log all types registered with the serialization manager)
            SerializationManager.LogRegisteredTypes();

            // 3. We scan types in memory for GrainTypeData objects that describe grain classes and their corresponding grain state classes.
            InitializeGrainClassData(loader, strict);

            // 4. We scan types in memory for grain method invoker objects.
            InitializeInvokerMap(loader, strict);

            InitializeInterfaceMap();
            StreamingInitialize();
        }

        public Dictionary<string, string> GetGrainInterfaceToClassMap()
        {
            return grainInterfaceMap.GetPrimaryImplementations();
        }

        internal GrainTypeData this[string className]
        {
            get
            {
                string msg;

                lock (this)
                {
                    string grainType;

                    if (grainInterfaceMap.TryGetPrimaryImplementation(className, out grainType))
                        return grainTypes[grainType];
                    if (grainTypes.ContainsKey(className))
                        return grainTypes[className];

                    if (TypeUtils.IsGenericClass(className))
                    {
                        var templateName = TypeUtils.GetRawClassName(className);
                        if (grainInterfaceMap.TryGetPrimaryImplementation(templateName, out grainType))
                            templateName = grainType;

                        if (grainTypes.ContainsKey(templateName))
                        {
                            // Found the generic template class
                            try
                            {
                                // Instantiate the specific type from generic template
                                var genericGrainTypeData = (GenericGrainTypeData)grainTypes[templateName];
                                Type[] typeArgs = TypeUtils.GenericTypeArgs(className);
                                var concreteTypeData = genericGrainTypeData.MakeGenericType(typeArgs);

                                // Add to lookup tables for next time
                                var grainClassName = concreteTypeData.GrainClass;
                                grainTypes.Add(grainClassName, concreteTypeData);

                                return concreteTypeData;
                            }
                            catch (Exception ex)
                            {
                                msg = "Cannot instantiate generic class " + className;
                                logger.Error(ErrorCode.Runtime_Error_100092, msg, ex);
                                throw new KeyNotFoundException(msg, ex);
                            }
                        }
                    }
                }

                msg = "Cannot find GrainTypeData for class " + className;
                logger.Error(ErrorCode.Runtime_Error_100093, msg);
                throw new TypeLoadException(msg);
            }
        }

        internal void GetTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, out MultiClusterRegistrationStrategy activationStrategy, string genericArguments = null)
        {
            if (!grainInterfaceMap.TryGetTypeInfo(typeCode, out grainClass, out placement, out activationStrategy, genericArguments))
                throw new OrleansException(String.Format("Unexpected: Cannot find an implementation class for grain interface {0}", typeCode));
        }

        private void InitializeGrainClassData(SiloAssemblyLoader loader, bool strict)
        {
            grainTypes = loader.GetGrainClassTypes(strict);
        }

        private void InitializeInvokerMap(SiloAssemblyLoader loader, bool strict)
        {
            IEnumerable<KeyValuePair<int, Type>> types = loader.GetGrainMethodInvokerTypes(strict);
            foreach (var i in types)
            {
                int ifaceId = i.Key;
                Type type = i.Value;
                AddInvokerClass(ifaceId, type);
            }
        }

        private void InitializeInterfaceMap()
        {
            foreach (GrainTypeData grainType in grainTypes.Values)
                AddToGrainInterfaceToClassMap(grainType.Type, grainType.RemoteInterfaceTypes, grainType.IsStatelessWorker);
        }

        private void AddToGrainInterfaceToClassMap(Type grainClass, IEnumerable<Type> grainInterfaces, bool isUnordered)
        {
            var grainClassCompleteName = TypeUtils.GetFullName(grainClass);
            var isGenericGrainClass = grainClass.ContainsGenericParameters;
            var grainClassTypeCode = CodeGeneration.GrainInterfaceUtils.GetGrainClassTypeCode(grainClass);
            var placement = GrainTypeData.GetPlacementStrategy(grainClass);
            var registrationStrategy = GrainTypeData.GetMultiClusterRegistrationStrategy(grainClass);

            foreach (var iface in grainInterfaces)
            {
                var ifaceCompleteName = TypeUtils.GetFullName(iface);
                var ifaceName = TypeUtils.GetRawClassName(ifaceCompleteName);
                var isPrimaryImplementor = IsPrimaryImplementor(grainClass, iface);
                var ifaceId = CodeGeneration.GrainInterfaceUtils.GetGrainInterfaceId(iface);
                grainInterfaceMap.AddEntry(ifaceId, iface, grainClassTypeCode, ifaceName, grainClassCompleteName,
                    grainClass.Assembly.CodeBase, isGenericGrainClass, placement, registrationStrategy, isPrimaryImplementor);
            }

            if (isUnordered)
                grainInterfaceMap.AddToUnorderedList(grainClassTypeCode);
        }

        private void StreamingInitialize()
        {
            SiloProviderRuntime.StreamingInitialize(grainFactory, new Streams.ImplicitStreamSubscriberTable());
            Type[] types = grainTypes.Values.Select(t => t.Type).ToArray();
            SiloProviderRuntime.Instance.ImplicitStreamSubscriberTable.InitImplicitStreamSubscribers(types);
        }

        private static bool IsPrimaryImplementor(Type grainClass, Type iface)
        {
            // If the class name exactly matches the interface name, it is considered the primary (default)
            // implementation of the interface, e.g. IFooGrain -> FooGrain
            return (iface.Name.Substring(1) == grainClass.Name);
        }

        public bool TryGetData(string name, out GrainTypeData result)
        {
            return grainTypes.TryGetValue(name, out result);
        }

        internal GrainInterfaceMap GetTypeCodeMap()
        {
            // the map is immutable at this point
            return grainInterfaceMap;
        }

        private void AddInvokerClass(int interfaceId, Type invoker)
        {
            lock (invokers)
            {
                if (!invokers.ContainsKey(interfaceId))
                    invokers.Add(interfaceId, new InvokerData(invoker));
            }
        }

        /// <summary>
        /// Returns a list of all graintypes in the system.
        /// </summary>
        /// <returns></returns>
        internal string[] GetGrainTypeList()
        {
            return grainTypes.Keys.ToArray();
        }

        internal IGrainMethodInvoker GetInvoker(int interfaceId, string genericGrainType = null)
        {
            try
            {
                InvokerData invokerData;
                if (invokers.TryGetValue(interfaceId, out invokerData))
                    return invokerData.GetInvoker(genericGrainType);
            }
            catch (Exception ex)
            {
                throw new OrleansException(String.Format("Error finding invoker for interface ID: {0} (0x{0, 8:X8}). {1}", interfaceId, ex), ex);
            }

            Type type;
            var interfaceName = grainInterfaceMap.TryGetServiceInterface(interfaceId, out type) ?
                type.FullName : "*unavailable*";

            throw new OrleansException(String.Format("Cannot find an invoker for interface {0} (ID={1},0x{1, 8:X8}).",
                interfaceName, interfaceId));
        }

        private class InvokerData
        {
            private readonly Type baseInvokerType;
            private IGrainMethodInvoker invoker;
            private readonly Dictionary<string, IGrainMethodInvoker> cachedGenericInvokers;
            private readonly object cachedGenericInvokersLockObj;

            public InvokerData(Type invokerType)
            {
                baseInvokerType = invokerType;
                if (invokerType.GetTypeInfo().IsGenericType)
                {
                    cachedGenericInvokers = new Dictionary<string, IGrainMethodInvoker>();
                    cachedGenericInvokersLockObj = new object(); ;
                }
            }

            public IGrainMethodInvoker GetInvoker(string genericGrainType = null)
            {
                if (String.IsNullOrEmpty(genericGrainType))
                    return invoker ?? (invoker = (IGrainMethodInvoker)Activator.CreateInstance(baseInvokerType));
                lock (cachedGenericInvokersLockObj)
                {
                    if (cachedGenericInvokers.ContainsKey(genericGrainType))
                        return cachedGenericInvokers[genericGrainType];
                }

                var typeArgs = TypeUtils.GenericTypeArgs(genericGrainType);
                var concreteType = baseInvokerType.MakeGenericType(typeArgs);
                var inv = (IGrainMethodInvoker)Activator.CreateInstance(concreteType);
                lock (cachedGenericInvokersLockObj)
                {
                    if (!cachedGenericInvokers.ContainsKey(genericGrainType))
                        cachedGenericInvokers[genericGrainType] = inv;
                }

                return inv;
            }
        }
    }
}
