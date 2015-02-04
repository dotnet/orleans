﻿/*
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
using System.Linq;

namespace Orleans.Runtime
{
    internal interface IGrainTypeResolver
    {
        bool TryGetGrainTypeCode(Type grainInterfaceType, out int grainTypeCode, string grainClassNamePrefix);
        bool TryGetGrainTypeCode(int grainInterfaceId, out int grainTypeCode, string grainClassNamePrefix);
        bool TryGetGrainTypeCode(string grainImplementationClassName, out int grainTypeCode);
        bool IsUnordered(int grainTypeCode);
        string GetLoadedGrainAssemblies();
    }

    /// <summary>
    /// Internal data structure that holds a grain interfaces to grain classes map.
    /// </summary>
    [Serializable]
    internal class GrainInterfaceMap : IGrainTypeResolver
    {
        private readonly Dictionary<string, GrainInterfaceData> typeToInterfaceData;
        private readonly Dictionary<int, GrainInterfaceData> table;
        private readonly HashSet<int> unordered;

        [NonSerialized]
        private readonly Dictionary<int, GrainClassData> implementationIndex;

        [NonSerialized] // Client shouldn't need this
        private readonly Dictionary<string, string> primaryImplementations;

        private readonly bool localTestMode;
        private readonly HashSet<string> loadedGrainAsemblies;

        public GrainInterfaceMap(bool localTestMode)
        {
            table = new Dictionary<int, GrainInterfaceData>();
            typeToInterfaceData = new Dictionary<string, GrainInterfaceData>();
            primaryImplementations = new Dictionary<string, string>();
            implementationIndex = new Dictionary<int, GrainClassData>();
            unordered = new HashSet<int>();
            this.localTestMode = localTestMode;
            if(localTestMode) // if we are running in test mode, we'll build a list of loaded grain assemblies to help with troubleshooting deployment issue
                loadedGrainAsemblies = new HashSet<string>();
        }

        internal void AddEntry(int interfaceId, Type iface, int grainTypeCode, string grainInterface, string grainClass, string assembly, 
                                PlacementStrategy placement, bool primaryImplementation = false)
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

                    if (iface.IsGenericType)
                    {
                        iface = iface.GetGenericTypeDefinition();
                    }

                    var key = iface.AssemblyQualifiedName;
                    typeToInterfaceData[key] = grainInterfaceData;
                }

                var implementation = new GrainClassData(grainTypeCode, grainClass, grainInterfaceData, placement);
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

        internal bool TryGetTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, string genericArguments = null)
        {
            lock (this)
            {
                grainClass = null;
                placement = null;

                if (!implementationIndex.ContainsKey(typeCode))
                    return false;

                var implementation = implementationIndex[typeCode];
                grainClass = genericArguments == null ? implementation.GrainClass : implementation.GetGenericClassName(genericArguments);
                placement = implementation.PlacementStrategy;
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

            grainClass = genericArguments == null ? implementation.GrainClass : implementation.GetGenericClassName(genericArguments);
            return true;
        }


        public bool TryGetGrainTypeCode(Type interfaceType, out int grainTypeCode, string grainClassNamePrefix)
        {
            if (interfaceType.IsGenericType)
            {
                interfaceType = interfaceType.GetGenericTypeDefinition();
            }

            grainTypeCode = 0;
            GrainInterfaceData interfaceData;
            var key = interfaceType.AssemblyQualifiedName;
            if (!this.typeToInterfaceData.TryGetValue(key, out interfaceData))
            {
                return false;
            }

            return TryGetGrainTypeCode(interfaceData, out grainTypeCode, grainClassNamePrefix);
        }

        public bool TryGetGrainTypeCode(int grainInterfaceId, out int grainTypeCode, string grainClassNamePrefix = null)
        {
            grainTypeCode = 0;
            GrainInterfaceData interfaceData;
            if (!table.TryGetValue(grainInterfaceId, out interfaceData))
            {
                return false;
            }

            return TryGetGrainTypeCode(interfaceData, out grainTypeCode, grainClassNamePrefix);
        }

        private static bool TryGetGrainTypeCode(GrainInterfaceData interfaceData, out int grainTypeCode, string grainClassNamePrefix)
        {
            grainTypeCode = 0;
            var implementations = interfaceData.Implementations;

            if (implementations.Length == 0)
                    return false;

            if (String.IsNullOrEmpty(grainClassNamePrefix))
            {
                if (implementations.Length == 1)
                {
                    grainTypeCode = implementations[0].GrainTypeCode;
                    return true;
                }

                if (interfaceData.PrimaryImplementation != null)
                {
                    grainTypeCode = interfaceData.PrimaryImplementation.GrainTypeCode;
                    return true;
                }

                throw new OrleansException(String.Format("Cannot resolve grain interface ID={0} to a grain class because of multiple implementations of it: {1}",
                    interfaceData.InterfaceId, Utils.EnumerableToString(implementations, d => d.GrainClass, ",", false)));
            }

            if (implementations.Length == 1)
            {
                if (implementations[0].GrainClass.StartsWith(grainClassNamePrefix, StringComparison.Ordinal))
                {
                    grainTypeCode = implementations[0].GrainTypeCode;
                    return true;
                }
                    
                return false;
            }

            var matches = implementations.Where(impl => impl.GrainClass.Equals(grainClassNamePrefix)).ToArray(); //exact match?
            if(matches.Length == 0)
                matches = implementations.Where(
                    impl => impl.GrainClass.StartsWith(grainClassNamePrefix, StringComparison.Ordinal)).ToArray(); //prefix matches

            if (matches.Length == 0)
                return false;

            if (matches.Length == 1)
            {
                grainTypeCode = matches[0].GrainTypeCode;
                return true;
            }

            throw new OrleansException(String.Format("Cannot resolve grain interface ID={0}, grainClassNamePrefix={1} to a grain class because of multiple implementations of it: {2}",
                interfaceData.InterfaceId, 
                grainClassNamePrefix,
                Utils.EnumerableToString(matches, d => d.GrainClass, ",", false)));
        }

        public bool TryGetGrainTypeCode(string grainImplementationClassName, out int grainTypeCode)
        {
            grainTypeCode = 0;
            // have to iterate since _primaryImplementations is not serialized.
            foreach (var interfaceData in table.Values)
            {
                foreach(var implClass in interfaceData.Implementations)
                    if (implClass.GrainClass.Equals(grainImplementationClassName))
                    {
                        grainTypeCode = implClass.GrainTypeCode;
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

    /// <summary>
    /// Metadata for a grain class
    /// </summary>
    [Serializable]
    internal class GrainClassData
    {
        [NonSerialized]
        private readonly GrainInterfaceData interfaceData;
        [NonSerialized]
        private readonly Dictionary<string, string> genericClassNames;
        private readonly PlacementStrategy placementStrategy;

        internal int GrainTypeCode { get; private set; }
        internal string GrainClass { get; private set; }
        internal PlacementStrategy PlacementStrategy { get { return placementStrategy; } }
        internal GrainInterfaceData InterfaceData { get { return interfaceData; } }

        internal GrainClassData(int grainTypeCode, string grainClass, GrainInterfaceData interfaceData, PlacementStrategy placement)
        {
            GrainTypeCode = grainTypeCode;
            GrainClass = grainClass;
            this.interfaceData = interfaceData;
            genericClassNames = new Dictionary<string, string>(); // TODO: initialize only for generic classes
            placementStrategy = placement ?? PlacementStrategy.GetDefault();
        }

        internal string GetGenericClassName(string typeArguments)
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
