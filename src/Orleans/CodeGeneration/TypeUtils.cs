using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Orleans.CodeGeneration;

namespace Orleans.Runtime
{
    /// <summary>
    /// A collection of utility functions for dealing with Type information.
    /// </summary>
    internal static class TypeUtils
    {
        /// <summary>
        /// The assembly name of the core Orleans assembly.
        /// </summary>
        private static readonly AssemblyName OrleansCoreAssembly = typeof(RuntimeVersion).GetTypeInfo().Assembly.GetName();

        private static readonly ConcurrentDictionary<Tuple<Type, TypeFormattingOptions>, string> ParseableNameCache = new ConcurrentDictionary<Tuple<Type, TypeFormattingOptions>, string>();

        private static readonly ConcurrentDictionary<Tuple<Type, bool>, List<Type>> ReferencedTypes = new ConcurrentDictionary<Tuple<Type, bool>, List<Type>>();

        public static string GetSimpleTypeName(Type t, Predicate<Type> fullName = null)
        {
            return GetSimpleTypeName(t.GetTypeInfo(), fullName);
        }

        public static string GetSimpleTypeName(TypeInfo typeInfo, Predicate<Type> fullName = null)
        {
            if (typeInfo.IsNestedPublic || typeInfo.IsNestedPrivate)
            {
                if (typeInfo.DeclaringType.GetTypeInfo().IsGenericType)
                {
                    return GetTemplatedName(
                        GetUntemplatedTypeName(typeInfo.DeclaringType.Name),
                        typeInfo.DeclaringType,
                        typeInfo.GetGenericArguments(),
                        _ => true) + "." + GetUntemplatedTypeName(typeInfo.Name);
                }

                return GetTemplatedName(typeInfo.DeclaringType) + "." + GetUntemplatedTypeName(typeInfo.Name);
            }

            var type = typeInfo.AsType();
            if (typeInfo.IsGenericType) return GetSimpleTypeName(fullName != null && fullName(type) ? GetFullName(type) : typeInfo.Name);

            return fullName != null && fullName(type) ? GetFullName(type) : typeInfo.Name;
        }

        public static string GetUntemplatedTypeName(string typeName)
        {
            int i = typeName.IndexOf('`');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('<');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            return typeName;
        }

        public static string GetSimpleTypeName(string typeName)
        {
            int i = typeName.IndexOf('`');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('[');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            i = typeName.IndexOf('<');
            if (i > 0)
            {
                typeName = typeName.Substring(0, i);
            }
            return typeName;
        }

        public static bool IsConcreteTemplateType(Type t)
        {
            if (t.GetTypeInfo().IsGenericType) return true;
            return t.IsArray && IsConcreteTemplateType(t.GetElementType());
        }

        public static string GetTemplatedName(Type t, Predicate<Type> fullName = null)
        {
            if (fullName == null)
                fullName = _ => true; // default to full type names

            var typeInfo = t.GetTypeInfo();
            if (typeInfo.IsGenericType) return GetTemplatedName(GetSimpleTypeName(typeInfo, fullName), t, typeInfo.GetGenericArguments(), fullName);

            if (t.IsArray)
            {
                return GetTemplatedName(t.GetElementType(), fullName)
                       + "["
                       + new string(',', t.GetArrayRank() - 1)
                       + "]";
            }

            return GetSimpleTypeName(typeInfo, fullName);
        }

        public static bool IsConstructedGenericType(this TypeInfo typeInfo)
        {
            // is there an API that returns this info without converting back to type already?
            return typeInfo.AsType().IsConstructedGenericType;
        }

        internal static IEnumerable<TypeInfo> GetTypeInfos(this Type[] types)
        {
            return types.Select(t => t.GetTypeInfo());
        }

        public static string GetTemplatedName(string baseName, Type t, Type[] genericArguments, Predicate<Type> fullName)
        {
            var typeInfo = t.GetTypeInfo();
            if (!typeInfo.IsGenericType || (t.DeclaringType != null && t.DeclaringType.GetTypeInfo().IsGenericType)) return baseName;
            string s = baseName;
            s += "<";
            s += GetGenericTypeArgs(genericArguments, fullName);
            s += ">";
            return s;
        }

        public static string GetGenericTypeArgs(IEnumerable<Type> args, Predicate<Type> fullName)
        {
            string s = string.Empty;

            bool first = true;
            foreach (var genericParameter in args)
            {
                if (!first)
                {
                    s += ",";
                }
                if (!genericParameter.GetTypeInfo().IsGenericType)
                {
                    s += GetSimpleTypeName(genericParameter, fullName);
                }
                else
                {
                    s += GetTemplatedName(genericParameter, fullName);
                }
                first = false;
            }

            return s;
        }

        public static string GetParameterizedTemplateName(TypeInfo typeInfo, bool applyRecursively = false, Predicate<Type> fullName = null)
        {
            if (fullName == null)
                fullName = tt => true;

            return GetParameterizedTemplateName(typeInfo, fullName, applyRecursively);
        }

        public static string GetParameterizedTemplateName(TypeInfo typeInfo, Predicate<Type> fullName, bool applyRecursively = false)
        {
            if (typeInfo.IsGenericType)
            {
                return GetParameterizedTemplateName(GetSimpleTypeName(typeInfo, fullName), typeInfo, applyRecursively, fullName);
            }
            
            var t = typeInfo.AsType();
            if (fullName != null && fullName(t) == true)
            {
                return t.FullName;
            }

            return t.Name;
        }

        public static string GetParameterizedTemplateName(string baseName, TypeInfo typeInfo, bool applyRecursively = false, Predicate<Type> fullName = null)
        {
            if (fullName == null)
                fullName = tt => false;

            if (!typeInfo.IsGenericType) return baseName;
            
            string s = baseName;
            s += "<";
            bool first = true;
            foreach (var genericParameter in typeInfo.GetGenericArguments())
            {
                if (!first)
                {
                    s += ",";
                }
                var genericParameterTypeInfo = genericParameter.GetTypeInfo();
                if (applyRecursively && genericParameterTypeInfo.IsGenericType)
                {
                    s += GetParameterizedTemplateName(genericParameterTypeInfo, applyRecursively);
                }
                else
                {
                    s += genericParameter.FullName == null || !fullName(genericParameter)
                        ? genericParameter.Name
                        : genericParameter.FullName;
                }
                first = false;
            }
            s += ">";
            return s;
        }

        public static string GetRawClassName(string baseName, Type t)
        {
            var typeInfo = t.GetTypeInfo();
            return typeInfo.IsGenericType ? baseName + '`' + typeInfo.GetGenericArguments().Length : baseName;
        }

        public static string GetRawClassName(string typeName)
        {
            int i = typeName.IndexOf('[');
            return i <= 0 ? typeName : typeName.Substring(0, i);
        }

        public static Type[] GenericTypeArgsFromClassName(string className)
        {
            return GenericTypeArgsFromArgsString(GenericTypeArgsString(className));
        }

        public static Type[] GenericTypeArgsFromArgsString(string genericArgs)
        {
            if (string.IsNullOrEmpty(genericArgs)) return Type.EmptyTypes;

            var genericTypeDef = genericArgs.Replace("[]", "##"); // protect array arguments

            return InnerGenericTypeArgs(genericTypeDef);
        }

        private static Type[] InnerGenericTypeArgs(string className)
        {
            var typeArgs = new List<Type>();
            var innerTypes = GetInnerTypes(className);

            foreach (var innerType in innerTypes)
            {
                if (innerType.StartsWith("[[")) // Resolve and load generic types recursively
                {
                    InnerGenericTypeArgs(GenericTypeArgsString(innerType));
                    string genericTypeArg = className.Trim('[', ']');
                    typeArgs.Add(Type.GetType(genericTypeArg.Replace("##", "[]")));
                }

                else
                {
                    string nonGenericTypeArg = innerType.Trim('[', ']');
                    typeArgs.Add(Type.GetType(nonGenericTypeArg.Replace("##", "[]")));
                }
            }

            return typeArgs.ToArray();
        }

        private static string[] GetInnerTypes(string input)
        {
            // Iterate over strings of length 2 positionwise.
            var charsWithPositions = input.Zip(Enumerable.Range(0, input.Length), (c, i) => new { Ch = c, Pos = i });
            var candidatesWithPositions = charsWithPositions.Zip(charsWithPositions.Skip(1), (c1, c2) => new { Str = c1.Ch.ToString() + c2.Ch, Pos = c1.Pos });

            var results = new List<string>();
            int startPos = -1;
            int endPos = -1;
            int endTokensNeeded = 0;
            string curStartToken = "";
            string curEndToken = "";
            var tokenPairs = new[] { new { Start = "[[", End = "]]" }, new { Start = "[", End = "]" } }; // Longer tokens need to come before shorter ones

            foreach (var candidate in candidatesWithPositions)
            {
                if (startPos == -1)
                {
                    foreach (var token in tokenPairs)
                    {
                        if (candidate.Str.StartsWith(token.Start))
                        {
                            curStartToken = token.Start;
                            curEndToken = token.End;
                            startPos = candidate.Pos;
                            break;
                        }
                    }
                }

                if (curStartToken != "" && candidate.Str.StartsWith(curStartToken))
                    endTokensNeeded++;

                if (curEndToken != "" && candidate.Str.EndsWith(curEndToken))
                {
                    endPos = candidate.Pos;
                    endTokensNeeded--;
                }

                if (endTokensNeeded == 0 && startPos != -1)
                {
                    results.Add(input.Substring(startPos, endPos - startPos + 2));
                    startPos = -1;
                    curStartToken = "";
                }
            }

            return results.ToArray();
        }

        public static string GenericTypeArgsString(string className)
        {
            int startIndex = className.IndexOf('[');
            int endIndex = className.LastIndexOf(']');
            return className.Substring(startIndex + 1, endIndex - startIndex - 1);
        }

        public static bool IsGenericClass(string name)
        {
            return name.Contains("`") || name.Contains("[");
        }

        public static string GetFullName(TypeInfo typeInfo)
        {
            if (typeInfo == null) throw new ArgumentNullException(nameof(typeInfo));
            return GetFullName(typeInfo.AsType());
        }

        public static string GetFullName(Type t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            if (t.IsNested && !t.IsGenericParameter)
            {
                return t.Namespace + "." + t.DeclaringType.Name + "." + t.Name;
            }
            if (t.IsArray)
            {
                return GetFullName(t.GetElementType())
                       + "["
                       + new string(',', t.GetArrayRank() - 1)
                       + "]";
            }
            return t.FullName ?? (t.IsGenericParameter ? t.Name : t.Namespace + "." + t.Name);
        }

        /// <summary>
        /// Returns all fields of the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>All fields of the specified type.</returns>
        public static IEnumerable<FieldInfo> GetAllFields(this Type type)
        {
            const BindingFlags AllFields =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var current = type;
            while ((current != typeof(object)) && (current != null))
            {
                var fields = current.GetFields(AllFields);
                foreach (var field in fields)
                {
                    yield return field;
                }

                current = current.GetTypeInfo().BaseType;
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> if <paramref name="field"/> is marked as
        /// <see cref="FieldAttributes.NotSerialized"/>, <see langword="false"/> otherwise.
        /// </summary>
        /// <param name="field">The field.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="field"/> is marked as
        /// <see cref="FieldAttributes.NotSerialized"/>, <see langword="false"/> otherwise.
        /// </returns>
        public static bool IsNotSerialized(this FieldInfo field)
            => (field.Attributes & FieldAttributes.NotSerialized) == FieldAttributes.NotSerialized;

        /// <summary>
        /// decide whether the class is derived from Grain
        /// </summary>
        public static bool IsGrainClass(Type type)
        {
            var grainType = typeof(Grain);
            var grainChevronType = typeof(Grain<>);

            if (type.Assembly.ReflectionOnly)
            {
                grainType = ToReflectionOnlyType(grainType);
                grainChevronType = ToReflectionOnlyType(grainChevronType);
            }

            if (grainType == type || grainChevronType == type) return false;

            if (!grainType.IsAssignableFrom(type)) return false;

            // exclude generated classes.
            return !IsGeneratedType(type);
        }

        public static bool IsConcreteGrainClass(Type type, out IEnumerable<string> complaints, bool complain)
        {
            complaints = null;
            if (!IsGrainClass(type)) return false;
            if (!type.GetTypeInfo().IsAbstract) return true;

            complaints = complain ? new[] { string.Format("Grain type {0} is abstract and cannot be instantiated.", type.FullName) } : null;
            return false;
        }

        public static bool IsConcreteGrainClass(Type type, out IEnumerable<string> complaints)
        {
            return IsConcreteGrainClass(type, out complaints, complain: true);
        }

        public static bool IsConcreteGrainClass(Type type)
        {
            IEnumerable<string> complaints;
            return IsConcreteGrainClass(type, out complaints, complain: false);
        }

        public static bool IsGeneratedType(Type type)
        {
            return TypeHasAttribute(type, typeof(GeneratedAttribute));
        }

        /// <summary>
        /// Returns true if the provided <paramref name="type"/> is in any of the provided
        /// <paramref name="namespaces"/>, false otherwise.
        /// </summary>
        /// <param name="type">The type to check.</param>
        /// <param name="namespaces"></param>
        /// <returns>
        /// true if the provided <paramref name="type"/> is in any of the provided <paramref name="namespaces"/>, false
        /// otherwise.
        /// </returns>
        public static bool IsInNamespace(Type type, List<string> namespaces)
        {
            if (type.Namespace == null)
            {
                return false;
            }

            foreach (var ns in namespaces)
            {
                if (ns.Length > type.Namespace.Length)
                {
                    continue;
                }

                // If the candidate namespace is a prefix of the type's namespace, return true.
                if (type.Namespace.StartsWith(ns, StringComparison.Ordinal)
                    && (type.Namespace.Length == ns.Length || type.Namespace[ns.Length] == '.'))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> has implementations of all serialization methods, false otherwise.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>
        /// true if <paramref name="type"/> has implementations of all serialization methods, false otherwise.
        /// </returns>
        public static bool HasAllSerializationMethods(Type type)
        {
            // Check if the type has any of the serialization methods.
            var hasCopier = false;
            var hasSerializer = false;
            var hasDeserializer = false;
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                hasSerializer |= method.GetCustomAttribute<SerializerMethodAttribute>(false) != null;
                hasDeserializer |= method.GetCustomAttribute<DeserializerMethodAttribute>(false) != null;
                hasCopier |= method.GetCustomAttribute<CopierMethodAttribute>(false) != null;
            }

            var hasAllSerializationMethods = hasCopier && hasSerializer && hasDeserializer;
            return hasAllSerializationMethods;
        }

        public static bool IsGrainMethodInvokerType(Type type)
        {
            var generalType = typeof(IGrainMethodInvoker);

            if (type.Assembly.ReflectionOnly)
            {
                generalType = ToReflectionOnlyType(generalType);
            }

            return generalType.IsAssignableFrom(type) && TypeHasAttribute(type, typeof(MethodInvokerAttribute));
        }

        public static Type ResolveType(string fullName)
        {
            return CachedTypeResolver.Instance.ResolveType(fullName);
        }

        public static bool TryResolveType(string fullName, out Type type)
        {
            return CachedTypeResolver.Instance.TryResolveType(fullName, out type);
        }

        private static Lazy<bool> canUseReflectionOnly = new Lazy<bool>(() =>
        {
            try
            {
                CachedReflectionOnlyTypeResolver.Instance.TryResolveType(typeof(TypeUtils).AssemblyQualifiedName, out _);
                return true;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
            catch (Exception)
            {
                // if other exceptions not related to platform ocurr, assume that ReflectionOnly is supported
                return true;
            }
        });

        public static bool CanUseReflectionOnly => canUseReflectionOnly.Value;

        public static Type ResolveReflectionOnlyType(string assemblyQualifiedName)
        {
            return CachedReflectionOnlyTypeResolver.Instance.ResolveType(assemblyQualifiedName);
        }

        public static Type ToReflectionOnlyType(Type type)
        {
            if (CanUseReflectionOnly)
            {
                return type.Assembly.ReflectionOnly ? type : ResolveReflectionOnlyType(type.AssemblyQualifiedName);
            }
            else
            {
                return type;
            }
        }

        public static IEnumerable<Type> GetTypes(Assembly assembly, Predicate<Type> whereFunc, Logger logger)
        {
            return assembly.IsDynamic ? Enumerable.Empty<Type>() : GetDefinedTypes(assembly, logger).Select(t => t.AsType()).Where(type => !type.GetTypeInfo().IsNestedPrivate && whereFunc(type));
        }

        public static IEnumerable<TypeInfo> GetDefinedTypes(Assembly assembly, Logger logger)
        {
            try
            {
                return assembly.DefinedTypes;
            }
            catch (Exception exception)
            {
                if (logger != null && logger.IsWarning)
                {
                    var message =
                        $"Exception loading types from assembly '{assembly.FullName}': {LogFormatter.PrintException(exception)}.";
                    logger.Warn(ErrorCode.Loader_TypeLoadError_5, message, exception);
                }
                
                var typeLoadException = exception as ReflectionTypeLoadException;
                if (typeLoadException != null)
                {
                    return typeLoadException.Types?.Where(type => type != null).Select(type => type.GetTypeInfo()) ??
                           Enumerable.Empty<TypeInfo>();
                }

                return Enumerable.Empty<TypeInfo>();
            }
        }

        public static IEnumerable<Type> GetTypes(Predicate<Type> whereFunc, Logger logger)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var result = new List<Type>();
            foreach (var assembly in assemblies)
            {
                // there's no point in evaluating nested private types-- one of them fails to coerce to a reflection-only type anyhow.
                var types = GetTypes(assembly, whereFunc, logger);
                result.AddRange(types);
            }
            return result;
        }

        /// <summary>
        /// Returns a value indicating whether or not the provided <paramref name="methodInfo"/> is a grain method.
        /// </summary>
        /// <param name="methodInfo">The method.</param>
        /// <returns>A value indicating whether or not the provided <paramref name="methodInfo"/> is a grain method.</returns>
        public static bool IsGrainMethod(MethodInfo methodInfo)
        {
            if (methodInfo == null) throw new ArgumentNullException("methodInfo", "Cannot inspect null method info");

            if (methodInfo.IsStatic || methodInfo.IsSpecialName || methodInfo.DeclaringType == null)
            {
                return false;
            }

            return methodInfo.DeclaringType.GetTypeInfo().IsInterface
                   && typeof(IAddressable).IsAssignableFrom(methodInfo.DeclaringType);
        }

        public static bool TypeHasAttribute(Type type, Type attribType)
        {
            if (type.Assembly.ReflectionOnly || attribType.Assembly.ReflectionOnly)
            {
                type = ToReflectionOnlyType(type);
                attribType = ToReflectionOnlyType(attribType);

                // we can't use Type.GetCustomAttributes here because we could potentially be working with a reflection-only type.
                return CustomAttributeData.GetCustomAttributes(type).Any(
                        attrib => attribType.IsAssignableFrom(attrib.AttributeType));
            }

            return TypeHasAttribute(type.GetTypeInfo(), attribType);
        }

        public static bool TypeHasAttribute(TypeInfo typeInfo, Type attribType)
        {
            return typeInfo.GetCustomAttributes(attribType, true).Any();
        }

        /// <summary>
        /// Returns a sanitized version of <paramref name="type"/>s name which is suitable for use as a class name.
        /// </summary>
        /// <param name="type">
        /// The grain type.
        /// </param>
        /// <returns>
        /// A sanitized version of <paramref name="type"/>s name which is suitable for use as a class name.
        /// </returns>
        public static string GetSuitableClassName(Type type)
        {
            return GetClassNameFromInterfaceName(type.GetUnadornedTypeName());
        }

        /// <summary>
        /// Returns a class-like version of <paramref name="interfaceName"/>.
        /// </summary>
        /// <param name="interfaceName">
        /// The interface name.
        /// </param>
        /// <returns>
        /// A class-like version of <paramref name="interfaceName"/>.
        /// </returns>
        public static string GetClassNameFromInterfaceName(string interfaceName)
        {
            string cleanName;
            if (interfaceName.StartsWith("i", StringComparison.OrdinalIgnoreCase))
            {
                cleanName = interfaceName.Substring(1);
            }
            else
            {
                cleanName = interfaceName;
            }

            return cleanName;
        }

        /// <summary>
        /// Returns the non-generic type name without any special characters.
        /// </summary>
        /// <param name="type">
        /// The type.
        /// </param>
        /// <returns>
        /// The non-generic type name without any special characters.
        /// </returns>
        public static string GetUnadornedTypeName(this Type type)
        {
            var index = type.Name.IndexOf('`');

            // An ampersand can appear as a suffix to a by-ref type.
            return (index > 0 ? type.Name.Substring(0, index) : type.Name).TrimEnd('&');
        }

        /// <summary>
        /// Returns the non-generic method name without any special characters.
        /// </summary>
        /// <param name="method">
        /// The method.
        /// </param>
        /// <returns>
        /// The non-generic method name without any special characters.
        /// </returns>
        public static string GetUnadornedMethodName(this MethodInfo method)
        {
            var index = method.Name.IndexOf('`');

            return index > 0 ? method.Name.Substring(0, index) : method.Name;
        }

        /// <summary>Returns a string representation of <paramref name="type"/>.</summary>
        /// <param name="type">The type.</param>
        /// <param name="options">The type formatting options.</param>
        /// <returns>A string representation of the <paramref name="type"/>.</returns>
        public static string GetParseableName(this Type type, TypeFormattingOptions options = null)
        {
            options = options ?? new TypeFormattingOptions();
            return ParseableNameCache.GetOrAdd(
                Tuple.Create(type, options),
                _ =>
                {
                    var builder = new StringBuilder();
                    var typeInfo = type.GetTypeInfo();
                    GetParseableName(
                        type,
                        builder,
                        new Queue<Type>(
                            typeInfo.IsGenericTypeDefinition
                                ? typeInfo.GetGenericArguments()
                                : typeInfo.GenericTypeArguments),
                        options);
                    return builder.ToString();
                });
        }

        /// <summary>Returns a string representation of <paramref name="type"/>.</summary>
        /// <param name="type">The type.</param>
        /// <param name="builder">The <see cref="StringBuilder"/> to append results to.</param>
        /// <param name="typeArguments">The type arguments of <paramref name="type"/>.</param>
        /// <param name="options">The type formatting options.</param>
        private static void GetParseableName(
            Type type,
            StringBuilder builder,
            Queue<Type> typeArguments,
            TypeFormattingOptions options)
        {
            var typeInfo = type.GetTypeInfo();
            if (typeInfo.IsArray)
            {
                builder.AppendFormat(
                    "{0}[{1}]",
                    typeInfo.GetElementType().GetParseableName(options),
                    string.Concat(Enumerable.Range(0, type.GetArrayRank() - 1).Select(_ => ',')));
                return;
            }

            if (typeInfo.IsGenericParameter)
            {
                if (options.IncludeGenericTypeParameters)
                {
                    builder.Append(type.GetUnadornedTypeName());
                }

                return;
            }

            if (typeInfo.DeclaringType != null)
            {
                // This is not the root type.
                GetParseableName(typeInfo.DeclaringType, builder, typeArguments, options);
                builder.Append(options.NestedTypeSeparator);
            }
            else if (!string.IsNullOrWhiteSpace(type.Namespace) && options.IncludeNamespace)
            {
                // This is the root type, so include the namespace.
                var namespaceName = type.Namespace;
                if (options.NestedTypeSeparator != '.')
                {
                    namespaceName = namespaceName.Replace('.', options.NestedTypeSeparator);
                }

                if (options.IncludeGlobal)
                {
                    builder.AppendFormat("global::");
                }

                builder.AppendFormat("{0}{1}", namespaceName, options.NestedTypeSeparator);
            }

            if (type.IsConstructedGenericType)
            {
                // Get the unadorned name, the generic parameters, and add them together.
                var unadornedTypeName = type.GetUnadornedTypeName() + options.NameSuffix;
                builder.Append(EscapeIdentifier(unadornedTypeName));
                var generics =
                    Enumerable.Range(0, Math.Min(typeInfo.GetGenericArguments().Count(), typeArguments.Count))
                        .Select(_ => typeArguments.Dequeue())
                        .ToList();
                if (generics.Count > 0 && options.IncludeTypeParameters)
                {
                    var genericParameters = string.Join(
                        ",",
                        generics.Select(generic => GetParseableName(generic, options)));
                    builder.AppendFormat("<{0}>", genericParameters);
                }
            }
            else if (typeInfo.IsGenericTypeDefinition)
            {
                // Get the unadorned name, the generic parameters, and add them together.
                var unadornedTypeName = type.GetUnadornedTypeName() + options.NameSuffix;
                builder.Append(EscapeIdentifier(unadornedTypeName));
                var generics =
                    Enumerable.Range(0, Math.Min(type.GetGenericArguments().Count(), typeArguments.Count))
                        .Select(_ => typeArguments.Dequeue())
                        .ToList();
                if (generics.Count > 0 && options.IncludeTypeParameters)
                {
                    var genericParameters = string.Join(
                        ",",
                        generics.Select(_ => options.IncludeGenericTypeParameters ? _.ToString() : string.Empty));
                    builder.AppendFormat("<{0}>", genericParameters);
                }
            }
            else
            {
                builder.Append(EscapeIdentifier(type.GetUnadornedTypeName() + options.NameSuffix));
            }
        }

        /// <summary>
        /// Returns the namespaces of the specified types.
        /// </summary>
        /// <param name="types">
        /// The types to include.
        /// </param>
        /// <returns>
        /// The namespaces of the specified types.
        /// </returns>
        public static IEnumerable<string> GetNamespaces(params Type[] types)
        {
            return types.Select(type => "global::" + type.Namespace).Distinct();
        }

        /// <summary>
        /// Returns the <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.
        /// </summary>
        /// <typeparam name="T">
        /// The containing type of the method.
        /// </typeparam>
        /// <typeparam name="TResult">
        /// The return type of the method.
        /// </typeparam>
        /// <param name="expression">
        /// The expression.
        /// </param>
        /// <returns>
        /// The <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.
        /// </returns>
        public static MethodInfo Method<T, TResult>(Expression<Func<T, TResult>> expression)
        {
            var methodCall = expression.Body as MethodCallExpression;
            if (methodCall != null)
            {
                return methodCall.Method;
            }

            throw new ArgumentException("Expression type unsupported.");
        }

        /// <summary>
        /// Returns the <see cref="PropertyInfo"/> for the simple member access in the provided <paramref name="expression"/>.
        /// </summary>
        /// <typeparam name="T">
        /// The containing type of the property.
        /// </typeparam>
        /// <typeparam name="TResult">
        /// The return type of the property.
        /// </typeparam>
        /// <param name="expression">
        /// The expression.
        /// </param>
        /// <returns>
        /// The <see cref="PropertyInfo"/> for the simple member access call in the provided <paramref name="expression"/>.
        /// </returns>
        public static PropertyInfo Property<T, TResult>(Expression<Func<T, TResult>> expression)
        {
            var property = expression.Body as MemberExpression;
            if (property != null)
            {
                return property.Member as PropertyInfo;
            }

            throw new ArgumentException("Expression type unsupported.");
        }
        
        /// <summary>
        /// Returns the <see cref="PropertyInfo"/> for the simple member access in the provided <paramref name="expression"/>.
        /// </summary>
        /// <typeparam name="TResult">
        /// The return type of the property.
        /// </typeparam>
        /// <param name="expression">
        /// The expression.
        /// </param>
        /// <returns>
        /// The <see cref="PropertyInfo"/> for the simple member access call in the provided <paramref name="expression"/>.
        /// </returns>
        public static PropertyInfo Property<TResult>(Expression<Func<TResult>> expression)
        {
            var property = expression.Body as MemberExpression;
            if (property != null)
            {
                return property.Member as PropertyInfo;
            }

            throw new ArgumentException("Expression type unsupported.");
        }

        /// <summary>
        /// Returns the <see cref="MemberInfo"/> for the simple member access in the provided <paramref name="expression"/>.
        /// </summary>
        /// <typeparam name="T">
        /// The containing type of the method.
        /// </typeparam>
        /// <typeparam name="TResult">
        /// The return type of the method.
        /// </typeparam>
        /// <param name="expression">
        /// The expression.
        /// </param>
        /// <returns>
        /// The <see cref="MemberInfo"/> for the simple member access call in the provided <paramref name="expression"/>.
        /// </returns>
        public static MemberInfo Member<T, TResult>(Expression<Func<T, TResult>> expression)
        {
            var methodCall = expression.Body as MethodCallExpression;
            if (methodCall != null)
            {
                return methodCall.Method;
            }

            var property = expression.Body as MemberExpression;
            if (property != null)
            {
                return property.Member;
            }

            throw new ArgumentException("Expression type unsupported.");
        }

        /// <summary>Returns the <see cref="MemberInfo"/> for the simple member access in the provided <paramref name="expression"/>.</summary>
        /// <typeparam name="TResult">The return type of the method.</typeparam>
        /// <param name="expression">The expression.</param>
        /// <returns>The <see cref="MemberInfo"/> for the simple member access call in the provided <paramref name="expression"/>.</returns>
        public static MemberInfo Member<TResult>(Expression<Func<TResult>> expression)
        {
            var methodCall = expression.Body as MethodCallExpression;
            if (methodCall != null)
            {
                return methodCall.Method;
            }

            var property = expression.Body as MemberExpression;
            if (property != null)
            {
                return property.Member;
            }

            throw new ArgumentException("Expression type unsupported.");
        }

        /// <summary>Returns the <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.</summary>
        /// <typeparam name="T">The containing type of the method.</typeparam>
        /// <param name="expression">The expression.</param>
        /// <returns>The <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.</returns>
        public static MethodInfo Method<T>(Expression<Func<T>> expression)
        {
            var methodCall = expression.Body as MethodCallExpression;
            if (methodCall != null)
            {
                return methodCall.Method;
            }

            throw new ArgumentException("Expression type unsupported.");
        }

        /// <summary>Returns the <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.
        /// </summary>
        /// <typeparam name="T">The containing type of the method.</typeparam>
        /// <param name="expression">The expression.</param>
        /// <returns>The <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.</returns>
        public static MethodInfo Method<T>(Expression<Action<T>> expression)
        {
            var methodCall = expression.Body as MethodCallExpression;
            if (methodCall != null)
            {
                return methodCall.Method;
            }

            throw new ArgumentException("Expression type unsupported.");
        }

        /// <summary>
        /// Returns the <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.
        /// </summary>
        /// <param name="expression">
        /// The expression.
        /// </param>
        /// <returns>
        /// The <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.
        /// </returns>
        public static MethodInfo Method(Expression<Action> expression)
        {
            var methodCall = expression.Body as MethodCallExpression;
            if (methodCall != null)
            {
                return methodCall.Method;
            }

            throw new ArgumentException("Expression type unsupported.");
        }

        /// <summary>Returns the namespace of the provided type, or <see cref="string.Empty"/> if the type has no namespace.</summary>
        /// <param name="type">The type.</param>
        /// <returns>The namespace of the provided type, or <see cref="string.Empty"/> if the type has no namespace.</returns>
        public static string GetNamespaceOrEmpty(this Type type)
        {
            if (type == null || string.IsNullOrEmpty(type.Namespace))
            {
                return string.Empty;
            }

            return type.Namespace;
        }

        /// <summary>Returns the types referenced by the provided <paramref name="type"/>.</summary>
        /// <param name="type">The type.</param>
        /// <param name="includeMethods">Whether or not to include the types referenced in the methods of this type.</param>
        /// <returns>The types referenced by the provided <paramref name="type"/>.</returns>
        public static IList<Type> GetTypes(this Type type, bool includeMethods = false)
        {
            List<Type> results;
            var key = Tuple.Create(type, includeMethods);
            if (!ReferencedTypes.TryGetValue(key, out results))
            {
                results = GetTypes(type, includeMethods, null).ToList();
                ReferencedTypes.TryAdd(key, results);
            }

            return results;
        }

        /// <summary>
        /// Get a public or non-public constructor that matches the constructor arguments signature
        /// </summary>
        /// <param name="type">The type to use.</param>
        /// <param name="constructorArguments">The constructor argument types to match for the signature.</param>
        /// <returns>A constructor that matches the signature or <see langword="null"/>.</returns>
        public static ConstructorInfo GetConstructorThatMatches(Type type, Type[] constructorArguments)
        {
            var constructorInfo = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                constructorArguments,
                null);
            return constructorInfo;
        }

        /// <summary>
        /// Returns a value indicating whether or not the provided assembly is the Orleans assembly or references it.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>A value indicating whether or not the provided assembly is the Orleans assembly or references it.</returns>
        internal static bool IsOrleansOrReferencesOrleans(Assembly assembly)
        {
            // We want to be loosely coupled to the assembly version if an assembly depends on an older Orleans,
            // but we want a strong assembly match for the Orleans binary itself 
            // (so we don't load 2 different versions of Orleans by mistake)
            return DoReferencesContain(assembly.GetReferencedAssemblies(), OrleansCoreAssembly)
                   || string.Equals(assembly.GetName().FullName, OrleansCoreAssembly.FullName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns a value indicating whether or not the specified references contain the provided assembly name.
        /// </summary>
        /// <param name="references">The references.</param>
        /// <param name="assemblyName">The assembly name.</param>
        /// <returns>A value indicating whether or not the specified references contain the provided assembly name.</returns>
        private static bool DoReferencesContain(IReadOnlyCollection<AssemblyName> references, AssemblyName assemblyName)
        {
            if (references.Count == 0)
            {
                return false;
            }

            return references.Any(asm => string.Equals(asm.Name, assemblyName.Name, StringComparison.Ordinal));
        }

        /// <summary>Returns the types referenced by the provided <paramref name="type"/>.</summary>
        /// <param name="type">The type.</param>
        /// <param name="includeMethods">Whether or not to include the types referenced in the methods of this type.</param>
        /// <param name="exclude">Types to exclude</param>
        /// <returns>The types referenced by the provided <paramref name="type"/>.</returns>
        private static IEnumerable<Type> GetTypes(
            this Type type,
            bool includeMethods,
            HashSet<Type> exclude)
        {
            exclude = exclude ?? new HashSet<Type>();
            if (!exclude.Add(type))
            {
                yield break;
            }

            yield return type;

            if (type.IsArray)
            {
                foreach (var elementType in type.GetElementType().GetTypes(false, exclude: exclude))
                {
                    yield return elementType;
                }
            }

            if (type.IsConstructedGenericType)
            {
                foreach (var genericTypeArgument in
                    type.GetGenericArguments().SelectMany(_ => GetTypes(_, false, exclude: exclude)))
                {
                    yield return genericTypeArgument;
                }
            }

            if (!includeMethods)
            {
                yield break;
            }

            foreach (var method in type.GetMethods())
            {
                foreach (var referencedType in GetTypes(method.ReturnType, false, exclude: exclude))
                {
                    yield return referencedType;
                }

                foreach (var parameter in method.GetParameters())
                {
                    foreach (var referencedType in GetTypes(parameter.ParameterType, false, exclude: exclude))
                    {
                        yield return referencedType;
                    }
                }
            }
        }

        private static string EscapeIdentifier(string identifier)
        {
            switch (identifier)
            {
                case "abstract":
                case "add":
                case "base":
                case "bool":
                case "break":
                case "byte":
                case "case":
                case "catch":
                case "char":
                case "checked":
                case "class":
                case "const":
                case "continue":
                case "decimal":
                case "default":
                case "delegate":
                case "do":
                case "double":
                case "else":
                case "enum":
                case "event":
                case "explicit":
                case "extern":
                case "false":
                case "finally":
                case "fixed":
                case "float":
                case "for":
                case "foreach":
                case "get":
                case "goto":
                case "if":
                case "implicit":
                case "in":
                case "int":
                case "interface":
                case "internal":
                case "lock":
                case "long":
                case "namespace":
                case "new":
                case "null":
                case "object":
                case "operator":
                case "out":
                case "override":
                case "params":
                case "partial":
                case "private":
                case "protected":
                case "public":
                case "readonly":
                case "ref":
                case "remove":
                case "return":
                case "sbyte":
                case "sealed":
                case "set":
                case "short":
                case "sizeof":
                case "static":
                case "string":
                case "struct":
                case "switch":
                case "this":
                case "throw":
                case "true":
                case "try":
                case "typeof":
                case "uint":
                case "ulong":
                case "unsafe":
                case "ushort":
                case "using":
                case "virtual":
                case "where":
                case "while":
                    return "@" + identifier;
                default:
                    return identifier;
            }
        }
    }
}
