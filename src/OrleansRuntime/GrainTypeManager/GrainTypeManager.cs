using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.GrainDirectory;
using Orleans.Runtime.Providers;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class GrainTypeManager
    {
        private IDictionary<string, GrainTypeData> grainTypes;
        private Dictionary<SiloAddress, GrainInterfaceMap> grainInterfaceMapsBySilo;
        private Dictionary<int, IList<SiloAddress>> supportedSilosByTypeCode;
        private readonly Logger logger = LogManager.GetLogger("GrainTypeManager");
        private readonly GrainInterfaceMap grainInterfaceMap;
        private readonly Dictionary<int, InvokerData> invokers = new Dictionary<int, InvokerData>();
        private readonly SiloAssemblyLoader loader;
        private static readonly object lockable = new object();
		private readonly PlacementStrategy defaultPlacementStrategy;

        internal IReadOnlyDictionary<SiloAddress, GrainInterfaceMap> GrainInterfaceMapsBySilo
        {
            get { return grainInterfaceMapsBySilo; }
        }

        public static GrainTypeManager Instance { get; private set; }

        public IEnumerable<KeyValuePair<string, GrainTypeData>> GrainClassTypeData { get { return grainTypes; } }

        public GrainInterfaceMap ClusterGrainInterfaceMap { get; private set; }

        public static void Stop()
        {
            Instance = null;
        }

        public GrainTypeManager(SiloInitializationParameters silo, SiloAssemblyLoader loader, DefaultPlacementStrategy defaultPlacementStrategy)
        {
            var localTestMode = silo.SiloAddress.Endpoint.Address.Equals(IPAddress.Loopback);

            this.defaultPlacementStrategy = defaultPlacementStrategy.PlacementStrategy;
            this.loader = loader;
            grainInterfaceMap = new GrainInterfaceMap(localTestMode, this.defaultPlacementStrategy);
            ClusterGrainInterfaceMap = grainInterfaceMap;
            grainInterfaceMapsBySilo = new Dictionary<SiloAddress, GrainInterfaceMap>();
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

            // (no more assemblies should be loaded into memory, so now is a good time to log all types registered with the serialization manager)
            SerializationManager.LogRegisteredTypes();

            // 3. We scan types in memory for GrainTypeData objects that describe grain classes and their corresponding grain state classes.
            InitializeGrainClassData(loader, strict);

            // 4. We scan types in memory for grain method invoker objects.
            InitializeInvokerMap(loader, strict);

            InitializeInterfaceMap();
        }

        public Dictionary<string, string> GetGrainInterfaceToClassMap()
        {
            return grainInterfaceMap.GetPrimaryImplementations();
        }

        internal bool TryGetPrimaryImplementation(string grainInterface, out string grainClass)
        {
            return grainInterfaceMap.TryGetPrimaryImplementation(grainInterface, out grainClass);
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
                                Type[] typeArgs = TypeUtils.GenericTypeArgsFromClassName(className);
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
            if (!ClusterGrainInterfaceMap.TryGetTypeInfo(typeCode, out grainClass, out placement, out activationStrategy, genericArguments))
                throw new OrleansException(String.Format("Unexpected: Cannot find an implementation class for grain interface {0}", typeCode));
        }

        internal void SetInterfaceMapsBySilo(Dictionary<SiloAddress, GrainInterfaceMap> value)
        {
            grainInterfaceMapsBySilo = value;
            RebuildFullGrainInterfaceMap();
        }

        internal IList<SiloAddress> GetSupportedSilos(int typeCode)
        {
            return supportedSilosByTypeCode[typeCode];
        }

        private void InitializeGrainClassData(SiloAssemblyLoader loader, bool strict)
        {
            grainTypes = loader.GetGrainClassTypes(strict);
            LogManager.GrainTypes = this.grainTypes.Keys.ToList();
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
            var grainTypeInfo = grainClass.GetTypeInfo();
            var grainClassCompleteName = TypeUtils.GetFullName(grainTypeInfo);
            var isGenericGrainClass = grainTypeInfo.ContainsGenericParameters;
            var grainClassTypeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grainClass);
            var placement = GrainTypeData.GetPlacementStrategy(grainClass, this.defaultPlacementStrategy);
            var registrationStrategy = GrainTypeData.GetMultiClusterRegistrationStrategy(grainClass);

            foreach (var iface in grainInterfaces)
            {
                var ifaceCompleteName = TypeUtils.GetFullName(iface);
                var ifaceName = TypeUtils.GetRawClassName(ifaceCompleteName);
                var isPrimaryImplementor = IsPrimaryImplementor(grainClass, iface);
                var ifaceId = GrainInterfaceUtils.GetGrainInterfaceId(iface);
                grainInterfaceMap.AddEntry(ifaceId, iface, grainClassTypeCode, ifaceName, grainClassCompleteName,
                    grainTypeInfo.Assembly.CodeBase, isGenericGrainClass, placement, registrationStrategy, isPrimaryImplementor);
            }

            if (isUnordered)
                grainInterfaceMap.AddToUnorderedList(grainClassTypeCode);
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

        private void RebuildFullGrainInterfaceMap()
        {
            var newClusterGrainInterfaceMap = new GrainInterfaceMap(false, defaultPlacementStrategy);
            var newSupportedSilosByTypeCode = new Dictionary<int, IList<SiloAddress>>();
            newClusterGrainInterfaceMap.AddMap(grainInterfaceMap);
            foreach (var kvp in grainInterfaceMapsBySilo)
            {
                newClusterGrainInterfaceMap.AddMap(kvp.Value);
                foreach (var grainType in kvp.Value.SupportedGrainTypes)
                {
                    IList<SiloAddress> supportedSilos;
                    if (!newSupportedSilosByTypeCode.TryGetValue(grainType, out supportedSilos))
                    {
                        newSupportedSilosByTypeCode[grainType] = supportedSilos = new List<SiloAddress>();
                    }
                    supportedSilos.Add(kvp.Key);
                }
            }
            ClusterGrainInterfaceMap = newClusterGrainInterfaceMap;
            supportedSilosByTypeCode = newSupportedSilosByTypeCode;
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
                // if the grain class is non-generic
                if (cachedGenericInvokersLockObj == null)
                {
                    return invoker ?? (invoker = (IGrainMethodInvoker)Activator.CreateInstance(baseInvokerType));
                }
                else
                {
                    lock (cachedGenericInvokersLockObj)
                    {
                        if (cachedGenericInvokers.ContainsKey(genericGrainType))
                            return cachedGenericInvokers[genericGrainType];
                    }
                    var typeArgs = TypeUtils.GenericTypeArgsFromArgsString(genericGrainType);
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
}
