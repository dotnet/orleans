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
        private Dictionary<int, List<SiloAddress>> supportedSilosByTypeCode;
        private readonly Logger logger = LogManager.GetLogger("GrainTypeManager");
        private readonly GrainInterfaceMap grainInterfaceMap;
        private readonly Dictionary<int, InvokerData> invokers = new Dictionary<int, InvokerData>();
        private readonly SiloAssemblyLoader loader;
        private readonly SerializationManager serializationManager;
        private readonly MultiClusterRegistrationStrategyManager multiClusterRegistrationStrategyManager;
		private readonly PlacementStrategy defaultPlacementStrategy;
        private Dictionary<int, Dictionary<ushort, List<SiloAddress>>> supportedSilosByInterface;

        internal IReadOnlyDictionary<SiloAddress, GrainInterfaceMap> GrainInterfaceMapsBySilo
        {
            get { return grainInterfaceMapsBySilo; }
        }

        public IEnumerable<KeyValuePair<string, GrainTypeData>> GrainClassTypeData { get { return grainTypes; } }

        public GrainInterfaceMap ClusterGrainInterfaceMap { get; private set; }
        
        public GrainTypeManager(ILocalSiloDetails siloDetails, SiloAssemblyLoader loader, DefaultPlacementStrategy defaultPlacementStrategy, SerializationManager serializationManager, MultiClusterRegistrationStrategyManager multiClusterRegistrationStrategyManager)
        {
            var localTestMode = siloDetails.SiloAddress.Endpoint.Address.Equals(IPAddress.Loopback);

            this.defaultPlacementStrategy = defaultPlacementStrategy.PlacementStrategy;
            this.loader = loader;
            this.serializationManager = serializationManager;
            this.multiClusterRegistrationStrategyManager = multiClusterRegistrationStrategyManager;
            grainInterfaceMap = new GrainInterfaceMap(localTestMode, this.defaultPlacementStrategy);
            ClusterGrainInterfaceMap = grainInterfaceMap;
            grainInterfaceMapsBySilo = new Dictionary<SiloAddress, GrainInterfaceMap>();
        }

        public void Start(bool strict = true)
        {
            // loading application assemblies now occurs in four phases.
            // 1. We scan the file system for assemblies meeting pre-determined criteria, specified in SiloAssemblyLoader.LoadApplicationAssemblies (called by the constructor).
            // 2. We load those assemblies into memory. In the official distribution of Orleans, this is usually 4 assemblies.

            // (no more assemblies should be loaded into memory, so now is a good time to log all types registered with the serialization manager)
            this.serializationManager.LogRegisteredTypes();

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

        internal IReadOnlyList<SiloAddress> GetSupportedSilos(int typeCode)
        {
            return supportedSilosByTypeCode[typeCode];
        }

        internal IReadOnlyDictionary<ushort, IReadOnlyList<SiloAddress>> GetSupportedSilos(int typeCode, int ifaceId, IReadOnlyList<ushort> versions)
        {
            var result = new Dictionary<ushort, IReadOnlyList<SiloAddress>>();
            foreach (var version in versions)
            {
                var silosWithTypeCode = supportedSilosByTypeCode[typeCode];
                // We need to sort this so the list of silos returned will
                // be the same accross all silos in the cluster
                var silosWithCorrectVersion = supportedSilosByInterface[ifaceId][version]
                    .Intersect(silosWithTypeCode)
                    .OrderBy(addr => addr)
                    .ToList();
                result[version] = silosWithCorrectVersion;
            }
            
            return result;
        }

        internal IReadOnlyList<ushort> GetAvailableVersions(int ifaceId)
        {
            return supportedSilosByInterface[ifaceId].Keys.ToList();
        }

        internal ushort GetLocalSupportedVersion(int ifaceId)
        {
            return grainInterfaceMap.GetInterfaceVersion(ifaceId);
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
            var placement = GrainTypeData.GetPlacementStrategy(grainClass, this.defaultPlacementStrategy);
            var registrationStrategy = this.multiClusterRegistrationStrategyManager.GetMultiClusterRegistrationStrategy(grainClass);

            foreach (var iface in grainInterfaces)
            {
                var isPrimaryImplementor = IsPrimaryImplementor(grainClass, iface);
                grainInterfaceMap.AddEntry(iface, grainClass, placement, registrationStrategy, isPrimaryImplementor);
            }

            if (isUnordered)
                grainInterfaceMap.AddToUnorderedList(grainClass);
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
            var newClusterGrainInterfaceMap = new GrainInterfaceMap(false, this.defaultPlacementStrategy);
            var newSupportedSilosByTypeCode = new Dictionary<int, List<SiloAddress>>();
            var newSupportedSilosByInterface = new Dictionary<int, Dictionary<ushort, List<SiloAddress>>>();
            foreach (var kvp in grainInterfaceMapsBySilo)
            {
                newClusterGrainInterfaceMap.AddMap(kvp.Value);

                foreach (var supportedInterface in kvp.Value.SupportedInterfaces)
                {
                    var ifaceId = supportedInterface.InterfaceId;
                    var version = supportedInterface.InterfaceVersion;

                    var supportedSilosByVersion = newSupportedSilosByInterface.GetValueOrAddNew(ifaceId);
                    var supportedSilosForVersion = supportedSilosByVersion.GetValueOrAddNew(version);
                    supportedSilosForVersion.Add(kvp.Key);
                }

                foreach (var grainClassData in kvp.Value.SupportedGrainClassData)
                {
                    var grainType = grainClassData.GrainTypeCode;

                    var supportedSilos = newSupportedSilosByTypeCode.GetValueOrAddNew(grainType);
                    supportedSilos.Add(kvp.Key);
                }
            }
            foreach (var silos in newSupportedSilosByTypeCode.Values)
            {
                // We need to sort this so the list of silos returned will
                // be the same accross all silos in the cluster
                silos.Sort(); 
            }
            ClusterGrainInterfaceMap = newClusterGrainInterfaceMap;
            supportedSilosByTypeCode = newSupportedSilosByTypeCode;
            supportedSilosByInterface = newSupportedSilosByInterface;
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
