using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    using System.Runtime.CompilerServices;

    internal static class TypeUtilities
    {
        internal static bool IsOrleansPrimitive(this Type t)
        {
            return t.IsPrimitive ||
                   t.IsEnum ||
                   t == typeof(string) ||
                   t == typeof(DateTime) ||
                   t == typeof(Decimal) ||
                   t == typeof(Guid) ||
                   (t.IsArray && t.GetElementType().IsOrleansPrimitive());
        }

        static readonly ConcurrentDictionary<Type, string> typeNameCache = new ConcurrentDictionary<Type, string>();
        static readonly ConcurrentDictionary<Type, string> typeKeyStringCache = new ConcurrentDictionary<Type, string>();
        static readonly ConcurrentDictionary<Type, byte[]> typeKeyCache = new ConcurrentDictionary<Type, byte[]>();

        static readonly ConcurrentDictionary<Type, bool> shallowCopyableTypes = new ConcurrentDictionary<Type, bool>
        {
            [typeof(Decimal)] = true,
            [typeof(DateTime)] = true,
            [typeof(TimeSpan)] = true,
            [typeof(IPAddress)] = true,
            [typeof(IPEndPoint)] = true,
            [typeof(SiloAddress)] = true,
            [typeof(GrainId)] = true,
            [typeof(ActivationId)] = true,
            [typeof(ActivationAddress)] = true,
            [typeof(CorrelationId)] = true,
            [typeof(string)] = true,
            [typeof(CancellationToken)] = true,
            [typeof(Guid)] = true,
        };

        internal static bool IsOrleansShallowCopyable(this Type t)
        {
            if (shallowCopyableTypes.TryGetValue(t, out var result))
            {
                return result;
            }
            return shallowCopyableTypes.GetOrAdd(t, IsShallowCopyableInternal(t));
        }

        private static bool IsShallowCopyableInternal(Type t)
        {
            if (t.IsPrimitive || t.IsEnum)
                return true;

            if (t.IsDefined(typeof(ImmutableAttribute), false))
                return true;

            if (t.IsConstructedGenericType)
            {
                var def = t.GetGenericTypeDefinition();

                if (def == typeof(Immutable<>))
                    return true;

                if (def == typeof(Nullable<>)
                    || def == typeof(Tuple<>)
                    || def == typeof(Tuple<,>)
                    || def == typeof(Tuple<,,>)
                    || def == typeof(Tuple<,,,>)
                    || def == typeof(Tuple<,,,,>)
                    || def == typeof(Tuple<,,,,,>)
                    || def == typeof(Tuple<,,,,,,>)
                    || def == typeof(Tuple<,,,,,,,>))
                    return Array.TrueForAll(t.GenericTypeArguments, a => IsOrleansShallowCopyable(a));
            }

            if (t.IsValueType && !t.IsGenericTypeDefinition)
                return Array.TrueForAll(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), f => IsOrleansShallowCopyable(f.FieldType));

            if (typeof(Exception).IsAssignableFrom(t))
                return true;

            return false;
        }

        internal static string OrleansTypeName(this Type t)
        {
            string name;
            if (typeNameCache.TryGetValue(t, out name))
                return name;

            name = TypeUtils.GetTemplatedName(t, _ => !_.IsGenericParameter);
            typeNameCache[t] = name;
            return name;
        }

        public static byte[] OrleansTypeKey(this Type t)
        {
            byte[] key;
            if (typeKeyCache.TryGetValue(t, out key))
                return key;

            key = Encoding.UTF8.GetBytes(t.OrleansTypeKeyString());
            typeKeyCache[t] = key;
            return key;
        }

        public static string OrleansTypeKeyString(this Type t)
        {
            string key;
            if (typeKeyStringCache.TryGetValue(t, out key))
                return key;

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
            typeKeyStringCache[t] = key;

            return key;
        }

        private static string GetBaseTypeKey(Type t)
        {
            string namespacePrefix = "";
            if ((t.Namespace != null) && !t.Namespace.StartsWith("System.", StringComparison.Ordinal) && !t.Namespace.Equals("System"))
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
        
        /// <summary>
        /// Returns <see langword="true"/> if a type is accessible from C# code from the specified assembly, and <see langword="false"/> otherwise.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="assembly"></param>
        /// <returns></returns>
        public static bool IsAccessibleFromAssembly(Type type, Assembly assembly)
        {
            if (type.IsSpecialName) return false;
            if (type.GetCustomAttribute<CompilerGeneratedAttribute>() != null) return false;

            // Obsolete types can be accessed, however obsolete types which have IsError set cannot.
            var obsoleteAttr = type.GetCustomAttribute<ObsoleteAttribute>();
            if (obsoleteAttr != null && obsoleteAttr.IsError) return false;

            // Arrays are accessible if their element type is accessible.
            if (type.IsArray) return IsAccessibleFromAssembly(type.GetElementType(), assembly);

            // Pointer and ref types are not accessible.
            if (type.IsPointer || type.IsByRef) return false;

            // Generic types are only accessible if their generic arguments are accessible.
            if (type.IsConstructedGenericType)
            {
                foreach (var parameter in type.GetGenericArguments())
                {
                    if (!IsAccessibleFromAssembly(parameter, assembly)) return false;
                }
            }
            else if (type.IsGenericTypeDefinition)
            {
                // Guard against unrepresentable type constraints, which appear when generating code for some languages, such as F#.
                foreach (var parameter in type.GetTypeInfo().GenericTypeParameters)
                {
                    foreach (var constraint in parameter.GetGenericParameterConstraints())
                    {
                        if (constraint == typeof(Array) || constraint == typeof(Delegate) || constraint == typeof(Enum)) return false;
                    }
                }
            }

            // Internal types are accessible only if the declaring assembly exposes its internals to the target assembly.
            if (type.IsNotPublic || type.IsNestedAssembly || type.IsNestedFamORAssem)
            {
                if (!AreInternalsVisibleTo(type.Assembly, assembly)) return false;
            }

            // Nested types which are private or protected are not accessible.
            if (type.IsNestedPrivate || type.IsNestedFamily || type.IsNestedFamANDAssem) return false;

            // Nested types are otherwise accessible if their declaring type is accessible.
            if (type.IsNested)
            {
                return IsAccessibleFromAssembly(type.DeclaringType, assembly);
            }

            return true;
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

            if (Equals(fromAssembly, toAssembly)) return true;

            // Check InternalsVisibleTo attributes on the from-assembly, pointing to the to-assembly.
            var fullName = toAssembly.GetName().FullName;
            var shortName = toAssembly.GetName().Name;
            var internalsVisibleTo = fromAssembly.GetCustomAttributes<InternalsVisibleToAttribute>();
            foreach (var attr in internalsVisibleTo)
            {
                if (string.Equals(attr.AssemblyName, fullName, StringComparison.Ordinal)) return true;
                if (string.Equals(attr.AssemblyName, shortName, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }
}
