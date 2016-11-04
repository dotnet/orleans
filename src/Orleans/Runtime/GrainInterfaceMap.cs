using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    internal interface IGrainTypeResolver
    {
        bool TryGetGrainClassData(Type grainInterfaceType, out GrainClassData implementation, string grainClassNamePrefix);
        bool TryGetGrainClassData(int grainInterfaceId, out GrainClassData implementation, string grainClassNamePrefix);
        bool TryGetGrainClassData(string grainImplementationClassName, out GrainClassData implementation);
        bool IsUnordered(int grainTypeCode);
        string GetLoadedGrainAssemblies();
    }

    /// <summary>
    /// Internal data structure that holds a grain interfaces to grain classes map.
    /// </summary>
    [Serializable]
    internal class GrainInterfaceMap : IGrainTypeResolver
    {
        /// <summary>
        /// Metadata for a grain interface
        /// </summary>
        [Serializable]
        internal class GrainInterfaceData
        {
            [NonSerialized]
            private readonly Type iface;
            private readonly HashSet<GrainClassData> implementations;

            internal Type Interface { get { return iface; } }
            internal int InterfaceId { get; private set; }
            internal string GrainInterface { get; private set; }
            internal GrainClassData[] Implementations { get { return implementations.ToArray(); } }
            internal GrainClassData PrimaryImplementation { get; private set; }

            internal GrainInterfaceData(int interfaceId, Type iface, string grainInterface)
            {
                InterfaceId = interfaceId;
                this.iface = iface;
                GrainInterface = grainInterface;
                implementations = new HashSet<GrainClassData>();
            }

            internal void AddImplementation(GrainClassData implementation, bool primaryImplemenation = false)
            {
                lock (this)
                {
                    if (!implementations.Contains(implementation))
                        implementations.Add(implementation);

                    if (primaryImplemenation)
                        PrimaryImplementation = implementation;
                }
            }

            public override string ToString()
            {
                return String.Format("{0}:{1}", GrainInterface, InterfaceId);
            }
        }

        private readonly Dictionary<string, GrainInterfaceData> typeToInterfaceData;
        private readonly Dictionary<int, GrainInterfaceData> table;
        private readonly HashSet<int> unordered;

        private readonly Dictionary<int, GrainClassData> implementationIndex;

        [NonSerialized] // Client shouldn't need this
        private readonly Dictionary<string, string> primaryImplementations;

        private readonly bool localTestMode;
        private readonly HashSet<string> loadedGrainAsemblies;
		
		private readonly PlacementStrategy defaultPlacementStrategy;

        internal IList<int> SupportedGrainTypes
        {
            get { return implementationIndex.Keys.ToList(); }
        }

        public GrainInterfaceMap(bool localTestMode, PlacementStrategy defaultPlacementStrategy)
        {
            table = new Dictionary<int, GrainInterfaceData>();
            typeToInterfaceData = new Dictionary<string, GrainInterfaceData>();
            primaryImplementations = new Dictionary<string, string>();
            implementationIndex = new Dictionary<int, GrainClassData>();
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
        }

        internal void AddEntry(int interfaceId, Type iface, int grainTypeCode, string grainInterface, string grainClass, string assembly, 
                                bool isGenericGrainClass, PlacementStrategy placement, MultiClusterRegistrationStrategy registrationStrategy, bool primaryImplementation = false)
        {
            lock (this)
            {
                GrainInterfaceData grainInterfaceData;

                if (table.ContainsKey(interfaceId))
                {
                    grainInterfaceData = table[interfaceId];
                }
                else
                {
                    grainInterfaceData = new GrainInterfaceData(interfaceId, iface, grainInterface);

                    table[interfaceId] = grainInterfaceData;
                    var interfaceTypeKey = GetTypeKey(iface, isGenericGrainClass);
                    typeToInterfaceData[interfaceTypeKey] = grainInterfaceData;
                }

                var implementation = new GrainClassData(grainTypeCode, grainClass, isGenericGrainClass, grainInterfaceData, placement, registrationStrategy);
                if (!implementationIndex.ContainsKey(grainTypeCode))
                    implementationIndex.Add(grainTypeCode, implementation);

                grainInterfaceData.AddImplementation(implementation, primaryImplementation);
                if (primaryImplementation)
                {
                    primaryImplementations[grainInterface] = grainClass;
                }
                else
                {
                    if (!primaryImplementations.ContainsKey(grainInterface))
                        primaryImplementations.Add(grainInterface, grainClass);
                }

                if (localTestMode)
                {
                    if (!loadedGrainAsemblies.Contains(assembly))
                        loadedGrainAsemblies.Add(assembly);
                }
            }
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

        internal bool ContainsGrainInterface(int interfaceId)
        {
            lock (this)
            {
                return table.ContainsKey(interfaceId);
            }
        }

        internal bool ContainsGrainImplementation(int typeCode)
        {
            lock (this)
            {
                return implementationIndex.ContainsKey(typeCode);
            }
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
                placement = implementation.PlacementStrategy ?? this.defaultPlacementStrategy;
                registrationStrategy = implementation.RegistrationStrategy;
                return true;
            }
        }

        internal bool TryGetGrainClass(int grainTypeCode, out string grainClass, string genericArguments)
        {
            grainClass = null;
            GrainClassData implementation;
            if (!implementationIndex.TryGetValue(grainTypeCode, out implementation))
            {
                return false;
            }

            grainClass = implementation.GetClassName(genericArguments);
            return true;
        }

        public bool TryGetGrainClassData(Type interfaceType, out GrainClassData implementation, string grainClassNamePrefix)
        {
            implementation = null;
            GrainInterfaceData interfaceData;
            var typeInfo = interfaceType.GetTypeInfo();

            // First, try to find a non-generic grain implementation:
            if (this.typeToInterfaceData.TryGetValue(GetTypeKey(interfaceType, false), out interfaceData) &&
                TryGetGrainClassData(interfaceData, out implementation, grainClassNamePrefix))
            {
                return true;
            }

            // If a concrete implementation was not found and the interface is generic, 
            // try to find a generic grain implementation:
            if (typeInfo.IsGenericType && 
                this.typeToInterfaceData.TryGetValue(GetTypeKey(interfaceType, true), out interfaceData) &&
                TryGetGrainClassData(interfaceData, out implementation, grainClassNamePrefix))
            {
                return true;
            }

            return false;
        }

        public bool TryGetGrainClassData(int grainInterfaceId, out GrainClassData implementation, string grainClassNamePrefix = null)
        {
            implementation = null;
            GrainInterfaceData interfaceData;
            if (!table.TryGetValue(grainInterfaceId, out interfaceData))
            {
                return false;
            }
            return TryGetGrainClassData(interfaceData, out implementation, grainClassNamePrefix);
        }

        private string GetTypeKey(Type interfaceType, bool isGenericGrainClass)
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

        private static bool TryGetGrainClassData(GrainInterfaceData interfaceData, out GrainClassData implementation, string grainClassNamePrefix)
        {
            implementation = null;
            var implementations = interfaceData.Implementations;

            if (implementations.Length == 0)
                return false;

            if (String.IsNullOrEmpty(grainClassNamePrefix))
            {
                if (implementations.Length == 1)
                {
                    implementation = implementations[0];
                    return true;
                }

                if (interfaceData.PrimaryImplementation != null)
                {
                    implementation = interfaceData.PrimaryImplementation;
                    return true;
                }

                throw new OrleansException(String.Format("Cannot resolve grain interface ID={0} to a grain class because of multiple implementations of it: {1}",
                    interfaceData.InterfaceId, Utils.EnumerableToString(implementations, d => d.GrainClass, ",", false)));
            }

            if (implementations.Length == 1)
            {
                if (implementations[0].GrainClass.StartsWith(grainClassNamePrefix, StringComparison.Ordinal))
                {
                    implementation = implementations[0];
                    return true;
                }

                return false;
            }

            var matches = implementations.Where(impl => impl.GrainClass.Equals(grainClassNamePrefix)).ToArray(); //exact match?
            if (matches.Length == 0)
                matches = implementations.Where(
                    impl => impl.GrainClass.StartsWith(grainClassNamePrefix, StringComparison.Ordinal)).ToArray(); //prefix matches

            if (matches.Length == 0)
                return false;

            if (matches.Length == 1)
            {
                implementation = matches[0];
                return true;
            }

            throw new OrleansException(String.Format("Cannot resolve grain interface ID={0}, grainClassNamePrefix={1} to a grain class because of multiple implementations of it: {2}",
                interfaceData.InterfaceId,
                grainClassNamePrefix,
                Utils.EnumerableToString(matches, d => d.GrainClass, ",", false)));
        }

        public bool TryGetGrainClassData(string grainImplementationClassName, out GrainClassData implementation)
        {
            implementation = null;
            // have to iterate since _primaryImplementations is not serialized.
            foreach (var interfaceData in table.Values)
            {
                foreach(var implClass in interfaceData.Implementations)
                    if (implClass.GrainClass.Equals(grainImplementationClassName))
                    {
                        implementation = implClass;
                        return true;
                    }
            }
            return false;
        }


        public string GetLoadedGrainAssemblies()
        {
            return loadedGrainAsemblies != null ? loadedGrainAsemblies.ToStrings() : String.Empty;
        }

        public void AddToUnorderedList(int grainClassTypeCode)
        {
            if (!unordered.Contains(grainClassTypeCode))
                unordered.Add(grainClassTypeCode);
    }


        public bool IsUnordered(int grainTypeCode)
        {
            return unordered.Contains(grainTypeCode);
        }
    }

    /// <summary>
    /// Metadata for a grain class
    /// </summary>
    [Serializable]
    internal sealed class GrainClassData
    {
        [NonSerialized]
        private readonly GrainInterfaceMap.GrainInterfaceData interfaceData;
        [NonSerialized]
        private readonly Dictionary<string, string> genericClassNames;

        private readonly PlacementStrategy placementStrategy;
        private readonly MultiClusterRegistrationStrategy registrationStrategy;
        private readonly bool isGeneric;

        internal int GrainTypeCode { get; private set; }
        internal string GrainClass { get; private set; }
        internal PlacementStrategy PlacementStrategy { get { return placementStrategy; } }
        internal GrainInterfaceMap.GrainInterfaceData InterfaceData { get { return interfaceData; } }
        internal bool IsGeneric { get { return isGeneric; } }
        public MultiClusterRegistrationStrategy RegistrationStrategy { get { return registrationStrategy; } }

        internal GrainClassData(int grainTypeCode, string grainClass, bool isGeneric, GrainInterfaceMap.GrainInterfaceData interfaceData, PlacementStrategy placement, MultiClusterRegistrationStrategy registrationStrategy)
        {
            GrainTypeCode = grainTypeCode;
            GrainClass = grainClass;
            this.isGeneric = isGeneric;
            this.interfaceData = interfaceData;
            genericClassNames = new Dictionary<string, string>(); // TODO: initialize only for generic classes
            placementStrategy = placement;
            this.registrationStrategy = registrationStrategy ?? MultiClusterRegistrationStrategy.GetDefault();
        }

        internal string GetClassName(string typeArguments)
        {
            // Knowing whether the grain implementation is generic allows for non-generic grain classes 
            // to implement one or more generic grain interfaces.
            // For generic grain classes, the assumption that they take the same generic arguments 
            // as the implemented generic interface(s) still holds.
            if (!isGeneric || String.IsNullOrWhiteSpace(typeArguments))
            {
                return GrainClass;
            }
            else
            {
                lock (this)
                {
                    if (genericClassNames.ContainsKey(typeArguments))
                        return genericClassNames[typeArguments];

                    var className = String.Format("{0}[{1}]", GrainClass, typeArguments);
                    genericClassNames.Add(typeArguments, className);
                    return className;
                }

            }
        }

        internal long GetTypeCode(Type interfaceType)
        {
            var typeInfo = interfaceType.GetTypeInfo();
            if (typeInfo.IsGenericType && this.IsGeneric)
            {
                string args = TypeUtils.GetGenericTypeArgs(typeInfo.GetGenericArguments(), t => true);
                int hash = Utils.CalculateIdHash(args);
                return (((long)(hash & 0x00FFFFFF)) << 32) + GrainTypeCode;
            }
            else
            {
                return GrainTypeCode;
            }
        }

        public override string ToString()
        {
            return String.Format("{0}:{1}", GrainClass, GrainTypeCode);
        }

        public override int GetHashCode()
        {
            return GrainTypeCode;
        }

        public override bool Equals(object obj)
        {
            if(!(obj is GrainClassData))
                return false;

            return GrainTypeCode == ((GrainClassData) obj).GrainTypeCode;
        }
    }
}
