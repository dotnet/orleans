using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

using Orleans.Runtime;
using Orleans.Concurrency;

namespace Orleans.Serialization
{
    using System.Runtime.CompilerServices;

    internal static class TypeUtilities
    {
        internal static bool IsOrleansPrimitive(this Type t)
        {
            var typeInfo = t.GetTypeInfo();
            return typeInfo.IsPrimitive || typeInfo.IsEnum || t == typeof(string) || t == typeof(DateTime) || t == typeof(Decimal) || (typeInfo.IsArray && typeInfo.GetElementType().IsOrleansPrimitive());
        }

        static readonly Dictionary<RuntimeTypeHandle, bool> shallowCopyableValueTypes = new Dictionary<RuntimeTypeHandle, bool>();
        static readonly Dictionary<RuntimeTypeHandle, string> typeNameCache = new Dictionary<RuntimeTypeHandle, string>();
        static readonly Dictionary<RuntimeTypeHandle, string> typeKeyStringCache = new Dictionary<RuntimeTypeHandle, string>();
        static readonly Dictionary<RuntimeTypeHandle, byte[]> typeKeyCache = new Dictionary<RuntimeTypeHandle, byte[]>();

        static TypeUtilities()
        {
            shallowCopyableValueTypes[typeof(Decimal).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(DateTime).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(TimeSpan).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(IPAddress).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(IPEndPoint).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(SiloAddress).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(GrainId).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(ActivationId).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(ActivationAddress).TypeHandle] = true;
            shallowCopyableValueTypes[typeof(CorrelationId).TypeHandle] = true;
        }

        internal static bool IsOrleansShallowCopyable(this Type t)
        {
            var typeInfo = t.GetTypeInfo();
            if (typeInfo.IsPrimitive || typeInfo.IsEnum || t == typeof (string) || t == typeof (DateTime) || t == typeof (Decimal) ||
                t == typeof (Immutable<>))
                return true;

            if (typeInfo.GetCustomAttributes(typeof (ImmutableAttribute), false).Length > 0) 
                return true;  

            if (typeInfo.IsGenericType && typeInfo.GetGenericTypeDefinition() == typeof (Immutable<>))
                return true;

            if (typeInfo.IsValueType && !typeInfo.IsGenericType && !typeInfo.IsGenericTypeDefinition)
            {
                bool result;
                lock (shallowCopyableValueTypes)
                {
                    if (shallowCopyableValueTypes.TryGetValue(typeInfo.TypeHandle, out result))
                        return result;
                }
                result = typeInfo.GetFields().All(f => !(f.FieldType == t) && f.FieldType.IsOrleansShallowCopyable());
                lock (shallowCopyableValueTypes)
                {
                    shallowCopyableValueTypes[t.TypeHandle] = result;
                }
                return result;
            }

            return false;
        }

        internal static bool IsSpecializationOf(this Type t, Type match)
        {
            var typeInfo = t.GetTypeInfo();
            return typeInfo.IsGenericType && typeInfo.GetGenericTypeDefinition() == match;
        }

        internal static string OrleansTypeName(this Type t)
        {
            string name;
            lock (typeNameCache)
            {
                if (typeNameCache.TryGetValue(t.TypeHandle, out name))
                    return name;
            }
            name = TypeUtils.GetTemplatedName(t, _ => !_.IsGenericParameter);
            lock (typeNameCache)
            {
                typeNameCache[t.TypeHandle] = name;
            }
            return name;
        }

        public static byte[] OrleansTypeKey(this Type t)
        {
            byte[] key;
            lock (typeKeyCache)
            {
                if (typeKeyCache.TryGetValue(t.TypeHandle, out key))
                    return key;
            }
            key = Encoding.UTF8.GetBytes(t.OrleansTypeKeyString());
            lock (typeNameCache)
            {
                typeKeyCache[t.TypeHandle] = key;
            }
            return key;
        }

        public static string OrleansTypeKeyString(this Type t)
        {
            var typeInfo = t.GetTypeInfo();
            string key;
            lock (typeKeyStringCache)
            {
                if (typeKeyStringCache.TryGetValue(typeInfo.TypeHandle, out key))
                    return key;
            }

            var sb = new StringBuilder();
            if (typeInfo.IsGenericTypeDefinition)
            {
                sb.Append(GetBaseTypeKey(t));
                sb.Append('\'');
                sb.Append(typeInfo.GetGenericArguments().Length);
            }
            else if (typeInfo.IsGenericType)
            {
                sb.Append(GetBaseTypeKey(t));
                sb.Append('<');
                var first = true;
                foreach (var genericArgument in t.GetGenericArguments())
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(OrleansTypeKeyString(genericArgument));
                }
                sb.Append('>');
            }
            else if (t.IsArray)
            {
                sb.Append(OrleansTypeKeyString(t.GetElementType()));
                sb.Append('[');
                if (t.GetArrayRank() > 1)
                {
                    sb.Append(',', t.GetArrayRank() - 1);
                }
                sb.Append(']');
            }
            else
            {
                sb.Append(GetBaseTypeKey(t));
            }

            key = sb.ToString();
            lock (typeKeyStringCache)
            {
                typeKeyStringCache[t.TypeHandle] = key;
            }

            return key;
        }

        private static string GetBaseTypeKey(Type t)
        {
            var typeInfo = t.GetTypeInfo();

            string namespacePrefix = "";
            if ((typeInfo.Namespace != null) && !typeInfo.Namespace.StartsWith("System.") && !typeInfo.Namespace.Equals("System"))
            {
                namespacePrefix = typeInfo.Namespace + '.';
            }

            if (typeInfo.IsNestedPublic)
            {
                return namespacePrefix + OrleansTypeKeyString(typeInfo.DeclaringType) + "." + typeInfo.Name;
            }

            return namespacePrefix + typeInfo.Name;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public static string GetLocationSafe(this Assembly a)
        {
            if (a.IsDynamic)
            {
                return "dynamic";
            }

            try
            {
                return a.Location;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        public static bool IsTypeIsInaccessibleForSerialization(Type type, Module fromModule, Assembly fromAssembly)
        {
            var typeInfo = type.GetTypeInfo();

            if (!typeInfo.IsVisible && typeInfo.IsConstructedGenericType)
            {
                foreach (var inner in typeInfo.GetGenericArguments())
                {
                    if (IsTypeIsInaccessibleForSerialization(inner, fromModule, fromAssembly))
                    {
                        return true;
                    }
                }
                
                return IsTypeIsInaccessibleForSerialization(
                    typeInfo.GetGenericTypeDefinition(),
                    fromModule,
                    fromAssembly);
            }

            if ((typeInfo.IsNotPublic || !typeInfo.IsVisible) && !AreInternalsVisibleTo(typeInfo.Assembly, fromAssembly))
            {
                // subtype is defined in a different assembly from the outer type
                if (!typeInfo.Module.Equals(fromModule))
                {
                    return true;
                }

                // subtype defined in a different assembly from the one we are generating serializers for.
                if (!typeInfo.Assembly.Equals(fromAssembly))
                {
                    return true;
                }
            }

            if (typeInfo.IsArray)
            {
                return IsTypeIsInaccessibleForSerialization(typeInfo.GetElementType(), fromModule, fromAssembly);
            }

            var result = typeInfo.IsNestedPrivate || typeInfo.IsNestedFamily || type.IsPointer;
            
            return result;
        }

        /// <summary>
        /// Returns true if <paramref name="fromAssembly"/> has exposed its internals to <paramref name="toAssembly"/>, false otherwise.
        /// </summary>
        /// <param name="fromAssembly">The assembly containing internal types.</param>
        /// <param name="toAssembly">The assembly requiring access to internal types.</param>
        /// <returns>
        /// true if <paramref name="fromAssembly"/> has exposed its internals to <paramref name="toAssembly"/>, false otherwise
        /// </returns>
        private static bool AreInternalsVisibleTo(Assembly fromAssembly, Assembly toAssembly)
        {
            // If the to-assembly is null, it cannot have internals visible to it.
            if (toAssembly == null)
            {
                return false;
            }

            // Check InternalsVisibleTo attributes on the from-assembly, pointing to the to-assembly.
            var serializationAssemblyName = toAssembly.GetName().FullName;
            var internalsVisibleTo = fromAssembly.GetCustomAttributes<InternalsVisibleToAttribute>();
            return internalsVisibleTo.Any(_ => _.AssemblyName == serializationAssemblyName);
        }
    }
}
