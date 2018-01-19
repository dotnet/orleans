using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    /// <summary>
    /// Internal data structure that holds a grain interfaces to grain classes map.
    /// </summary>
    [Serializable]
    internal class GrainInterfaceMap
    {
        private readonly Dictionary<string, GrainInterfaceData> typeToInterfaceData;
        private readonly Dictionary<int, GrainInterfaceData> table;
        private readonly HashSet<int> unordered;

        private readonly Dictionary<int, GrainClassData> implementationIndex;
        private readonly Dictionary<int, PlacementStrategy> placementStrategiesIndex;
        private readonly Dictionary<int, MultiClusterRegistrationStrategy> registrationStrategiesIndex;

        [NonSerialized] // Client shouldn't need this
        private readonly Dictionary<string, string> primaryImplementations;

        private readonly bool localTestMode;
        private readonly HashSet<string> loadedGrainAsemblies;
		
		private readonly PlacementStrategy defaultPlacementStrategy;

        internal IEnumerable<GrainClassData> SupportedGrainClassData
        {
            get { return implementationIndex.Values; }
        }

        internal IEnumerable<GrainInterfaceData> SupportedInterfaces
        {
            get { return table.Values; }
        }

        public GrainInterfaceMap(bool localTestMode, PlacementStrategy defaultPlacementStrategy)
        {
            table = new Dictionary<int, GrainInterfaceData>();
            typeToInterfaceData = new Dictionary<string, GrainInterfaceData>();
            primaryImplementations = new Dictionary<string, string>();
            implementationIndex = new Dictionary<int, GrainClassData>();
            placementStrategiesIndex = new Dictionary<int, PlacementStrategy>();
            registrationStrategiesIndex = new Dictionary<int, MultiClusterRegistrationStrategy>();
            unordered = new HashSet<int>();
            this.localTestMode = localTestMode;
            this.defaultPlacementStrategy = defaultPlacementStrategy;
            if(localTestMode) // if we are running in test mode, we'll build a list of loaded grain assemblies to help with troubleshooting deployment issue
                loadedGrainAsemblies = new HashSet<string>();
        }

        internal void AddMap(GrainInterfaceMap map)
        {
            foreach (var kvp in map.typeToInterfaceData)
            {
                if (!typeToInterfaceData.ContainsKey(kvp.Key))
                {
                    typeToInterfaceData.Add(kvp.Key, kvp.Value);
                }
            }

            foreach (var kvp in map.table)
            {
                if (!table.ContainsKey(kvp.Key))
                {
                    table.Add(kvp.Key, kvp.Value);
                }
            }

            foreach (var grainClassTypeCode in map.unordered)
            {
                unordered.Add(grainClassTypeCode);
            }

            foreach (var kvp in map.implementationIndex)
            {
                if (!implementationIndex.ContainsKey(kvp.Key))
                {
                    implementationIndex.Add(kvp.Key, kvp.Value);
                }
            }

            foreach (var kvp in map.placementStrategiesIndex)
            {
                if (!placementStrategiesIndex.ContainsKey(kvp.Key))
                {
                    placementStrategiesIndex.Add(kvp.Key, kvp.Value);
                }
            }

            foreach (var kvp in map.registrationStrategiesIndex)
            {
                if (!registrationStrategiesIndex.ContainsKey(kvp.Key))
                {
                    registrationStrategiesIndex.Add(kvp.Key, kvp.Value);
                }
            }
        }

        internal void AddEntry(Type iface, Type grain, PlacementStrategy placement, MultiClusterRegistrationStrategy registrationStrategy, bool primaryImplementation)
        {
            lock (this)
            {
                var grainTypeInfo = grain.GetTypeInfo();
                var grainName = TypeUtils.GetFullName(grainTypeInfo);
                var isGenericGrainClass = grainTypeInfo.ContainsGenericParameters;
                var grainTypeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grain);

                var grainInterfaceData = GetOrAddGrainInterfaceData(iface, isGenericGrainClass);

                var implementation = new GrainClassData(grainTypeCode, grainName, isGenericGrainClass);
                if (!implementationIndex.ContainsKey(grainTypeCode))
                    implementationIndex.Add(grainTypeCode, implementation);
                if (!placementStrategiesIndex.ContainsKey(grainTypeCode))
                    placementStrategiesIndex.Add(grainTypeCode, placement);
                if (!registrationStrategiesIndex.ContainsKey(grainTypeCode))
                    registrationStrategiesIndex.Add(grainTypeCode, registrationStrategy);

                grainInterfaceData.AddImplementation(implementation, primaryImplementation);
                if (primaryImplementation)
                {
                    primaryImplementations[grainInterfaceData.GrainInterface] = grainName;
                }
                else
                {
                    if (!primaryImplementations.ContainsKey(grainInterfaceData.GrainInterface))
                        primaryImplementations.Add(grainInterfaceData.GrainInterface, grainName);
                }

                if (localTestMode)
                {
                    var assembly = grainTypeInfo.Assembly.CodeBase;
                    if (!loadedGrainAsemblies.Contains(assembly))
                        loadedGrainAsemblies.Add(assembly);
                }
            }
        }

        private GrainInterfaceData GetOrAddGrainInterfaceData(Type iface, bool isGenericGrainClass)
        {
            var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(iface);
            var version = GrainInterfaceUtils.GetGrainInterfaceVersion(iface);

            // If already exist
            GrainInterfaceData grainInterfaceData;
            if (table.TryGetValue(interfaceId, out grainInterfaceData))
                return grainInterfaceData;

            // If not create new entry
            var interfaceName = TypeUtils.GetRawClassName(TypeUtils.GetFullName(iface));
            grainInterfaceData = new GrainInterfaceData(interfaceId, version, iface, interfaceName);
            table[interfaceId] = grainInterfaceData;

            // Add entry to mapping iface string -> data
            var interfaceTypeKey = GetTypeKey(iface, isGenericGrainClass);
            typeToInterfaceData[interfaceTypeKey] = grainInterfaceData;

            // If we are adding a concrete implementation of a generic interface
            // add also the latter to the map: GrainReference and InvokeMethodRequest 
            // always use the id of the generic one
            if (iface.IsConstructedGenericType)
                GetOrAddGrainInterfaceData(iface.GetGenericTypeDefinition(), true);

            return grainInterfaceData;
        }

        internal Dictionary<string, string> GetPrimaryImplementations()
        {
            lock (this)
            {
                return new Dictionary<string, string>(primaryImplementations);
            }
        }

        internal bool TryGetPrimaryImplementation(string grainInterface, out string grainClass)
        {
            lock (this)
            {
                return primaryImplementations.TryGetValue(grainInterface, out grainClass);
            }
        }

        internal bool TryGetServiceInterface(int interfaceId, out Type iface)
        {
            lock (this)
            {
                iface = null;

                if (!table.ContainsKey(interfaceId))
                    return false;

                var interfaceData = table[interfaceId];
                iface = interfaceData.Interface;
                return true;
            }
        }

        internal ushort GetInterfaceVersion(int ifaceId)
        {
            return table[ifaceId].InterfaceVersion;
        }

        internal bool TryGetTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, out MultiClusterRegistrationStrategy registrationStrategy, string genericArguments = null)
        {
            lock (this)
            {
                grainClass = null;
                placement = this.defaultPlacementStrategy;
                registrationStrategy = null;
                if (!implementationIndex.ContainsKey(typeCode))
                    return false;

                var implementation = implementationIndex[typeCode];
                grainClass = implementation.GetClassName(genericArguments);
                placement = placementStrategiesIndex[typeCode];
                registrationStrategy = registrationStrategiesIndex[typeCode];
                return true;
            }
        }

        internal static string GetTypeKey(Type interfaceType, bool isGenericGrainClass)
        {
            var typeInfo = interfaceType.GetTypeInfo();
            if (isGenericGrainClass && typeInfo.IsGenericType)
            {
                return typeInfo.GetGenericTypeDefinition().AssemblyQualifiedName;
            }
            else 
            {
                return TypeUtils.GetTemplatedName(
                            TypeUtils.GetFullName(interfaceType),
                            interfaceType,
                            interfaceType.GetGenericArguments(),
                            t => false);
            }
        }

        public void AddToUnorderedList(Type grainClass)
        {
            var grainClassTypeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grainClass);
            if (!unordered.Contains(grainClassTypeCode))
                unordered.Add(grainClassTypeCode);
        }

        public bool IsUnordered(int grainTypeCode)
        {
            return unordered.Contains(grainTypeCode);
        }

        public IGrainTypeResolver GetGrainTypeResolver()
        {
            return new GrainTypeResolver(
                this.typeToInterfaceData,
                this.table,
                this.loadedGrainAsemblies,
                this.unordered
                );
        }
    }
}
