using System;
using System.Collections.Generic;
using System.Reflection;

namespace Orleans.Runtime
{
    /// <summary>
    /// Metadata for a grain class
    /// </summary>
    [Serializable]
    internal sealed class GrainClassData
    {
        [NonSerialized]
        private readonly Dictionary<string, string> genericClassNames;

        private readonly bool isGeneric;

        internal int GrainTypeCode { get; private set; }
        internal string GrainClass { get; private set; }
        internal bool IsGeneric { get { return isGeneric; } }

        internal GrainClassData(int grainTypeCode, string grainClass, bool isGeneric)
        {
            GrainTypeCode = grainTypeCode;
            GrainClass = grainClass;
            this.isGeneric = isGeneric;
            genericClassNames = new Dictionary<string, string>(); // TODO: initialize only for generic classes
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
