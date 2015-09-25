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
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(DateTime) || t == typeof(Decimal) || (t.IsArray && t.GetElementType().IsOrleansPrimitive());
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
            if (t.IsPrimitive || t.IsEnum || t == typeof (string) || t == typeof (DateTime) || t == typeof (Decimal) ||
                t == typeof (Immutable<>))
                return true;

            if (t.GetCustomAttributes(typeof (ImmutableAttribute), false).Length > 0) 
                return true;  

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof (Immutable<>))
                return true;

            if (t.IsValueType && !t.IsGenericType && !t.IsGenericTypeDefinition)
            {
                bool result;
                lock (shallowCopyableValueTypes)
                {
                    if (shallowCopyableValueTypes.TryGetValue(t.TypeHandle, out result))
                        return result;
                }
                result = t.GetFields().All(f => !(f.FieldType == t) && f.FieldType.IsOrleansShallowCopyable());
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
            return t.IsGenericType && t.GetGenericTypeDefinition() == match;
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
            string key;
            lock (typeKeyStringCache)
            {
                if (typeKeyStringCache.TryGetValue(t.TypeHandle, out key))
                    return key;
            }

            var sb = new StringBuilder();
            if (t.IsGenericTypeDefinition)
            {
                sb.Append(GetBaseTypeKey(t));
                sb.Append('\'');
                sb.Append(t.GetGenericArguments().Length);
            }
            else if (t.IsGenericType)
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
            string namespacePrefix = "";
            if ((t.Namespace != null) && !t.Namespace.StartsWith("System.") && !t.Namespace.Equals("System"))
            {
                namespacePrefix = t.Namespace + '.';
            }

            if (t.IsNestedPublic)
            {
                return namespacePrefix + OrleansTypeKeyString(t.DeclaringType) + "." + t.Name;
            }

            return namespacePrefix + t.Name;
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

        public static bool IsTypeIsInaccessibleForSerialization(Type t, Module fromModule, Assembly fromAssembly)
        {
            if (!t.IsVisible && t.IsConstructedGenericType)
            {
                foreach (var inner in t.GetGenericArguments())
                {
                    if (IsTypeIsInaccessibleForSerialization(inner, fromModule, fromAssembly))
                    {
                        return true;
                    }
                }
                
                return IsTypeIsInaccessibleForSerialization(
                    t.GetGenericTypeDefinition(),
                    fromModule,
                    fromAssembly);
            }

            if ((t.IsNotPublic || !t.IsVisible) && !AreInternalsVisibleTo(t.Assembly, fromAssembly))
            {
                // subtype is defined in a different assembly from the outer type
                if (!t.Module.Equals(fromModule))
                {
                    return true;
                }

                // subtype defined in a different assembly from the one we are generating serializers for.
                if (!t.Assembly.Equals(fromAssembly))
                {
                    return true;
                }
            }

            if (t.IsArray)
            {
                return IsTypeIsInaccessibleForSerialization(t.GetElementType(), fromModule, fromAssembly);
            }

            var result = t.IsNestedPrivate || t.IsNestedFamily;
            
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
            if (internalsVisibleTo.All(_ => _.AssemblyName != serializationAssemblyName))
            {
                return true;
            }

            return false;
        }
    }
}