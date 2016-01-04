using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.CodeGeneration;
using System.Linq.Expressions;
using System.Text;

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
        private static readonly string OrleansCoreAssembly = typeof(IGrain).Assembly.GetName().FullName;

        private static readonly ConcurrentDictionary<Tuple<Type, string, bool, bool>, string> ParseableNameCache = new ConcurrentDictionary<Tuple<Type, string, bool, bool>, string>();

        private static readonly ConcurrentDictionary<Tuple<Type, bool>, List<Type>> ReferencedTypes = new ConcurrentDictionary<Tuple<Type, bool>, List<Type>>();

        private static string GetSimpleNameHandleArray(Type t, Language language)
        {
            if (t.IsArray && language == Language.VisualBasic)
                return t.Name.Replace('[', '(').Replace(']', ')');

            return t.Name;
        }
        
        public static string GetSimpleTypeName(Type t, Func<Type, bool> fullName=null, Language language = Language.CSharp)
        {
            var typeInfo = t.GetTypeInfo();
            if (typeInfo.IsNestedPublic || typeInfo.IsNestedPrivate)
            {
                if (typeInfo.DeclaringType.IsGenericType)
                    return GetTemplatedName(GetUntemplatedTypeName(typeInfo.DeclaringType.Name), typeInfo.DeclaringType, typeInfo.GetGenericArguments(), _ => true, language) + "." + GetUntemplatedTypeName(typeInfo.Name);
                
                return GetTemplatedName(typeInfo.DeclaringType, language: language) + "." + GetUntemplatedTypeName(typeInfo.Name);
            }

            if (typeInfo.IsGenericType) return GetSimpleTypeName(fullName != null && fullName(t) ? GetFullName(t, language) : GetSimpleNameHandleArray(t, language));
            
            return fullName != null && fullName(t) ? GetFullName(t, language) : GetSimpleNameHandleArray(t, language: language);
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
            if (t.IsGenericType) return true;
            return t.IsArray && IsConcreteTemplateType(t.GetElementType());
        }

        public static string GetTemplatedName(Type t, Func<Type, bool> fullName=null, Language language = Language.CSharp)
        {
            if (fullName == null)
                fullName = _ => true; // default to full type names

            if (t.IsGenericType) return GetTemplatedName(GetSimpleTypeName(t, fullName, language), t, t.GetGenericArguments(), fullName, language);

            if (t.IsArray)
            {
                bool isVB = language == Language.VisualBasic;

                return GetTemplatedName(t.GetElementType(), fullName)
                       + (isVB ? "(" : "[")
                       + new string(',', t.GetArrayRank() - 1)
                       + (isVB ? ")" : "]");
            }
            
            return GetSimpleTypeName(t, fullName, language);
        }

        public static string GetTemplatedName(string baseName, Type t, Type[] genericArguments, Func<Type, bool> fullName, Language language = Language.CSharp)
        {
            var typeInfo = t.GetTypeInfo();
            if (!typeInfo.IsGenericType || (typeInfo.DeclaringType != null && typeInfo.DeclaringType.IsGenericType)) return baseName;
            bool isVB = language == Language.VisualBasic;
            string s = baseName;
            s += isVB ? "(Of " : "<";
            s += GetGenericTypeArgs(genericArguments, fullName, language);
            s += isVB ? ")" : ">";
            return s;
        }

        public static string GetGenericTypeArgs(Type[] args, Func<Type, bool> fullName, Language language = Language.CSharp)
        {
            string s = string.Empty;

            bool first = true;
            foreach (var genericParameter in args)
            {
                if (!first)
                {
                    s += ",";
                }
                if (!genericParameter.IsGenericType)
                {
                    s += GetSimpleTypeName(genericParameter, fullName, language);
                }
                else
                {
                    s += GetTemplatedName(genericParameter, fullName, language);
                }
                first = false;
            }

            return s;
        }

        public static string GetParameterizedTemplateName(Type t, bool applyRecursively = false, Func<Type, bool> fullName = null, Language language = Language.CSharp)
        {
            if (fullName == null)
                fullName = tt => true;

            return GetParameterizedTemplateName(t, fullName, applyRecursively, language);
        }

        public static string GetParameterizedTemplateName(Type t, Func<Type, bool> fullName, bool applyRecursively = false, Language language = Language.CSharp)
        {
            if (t.IsGenericType)
            {
                return GetParameterizedTemplateName(GetSimpleTypeName(t, fullName), t, applyRecursively, fullName, language);
            }
            else
            {
                if(fullName != null && fullName(t)==true)
                {
                    return t.FullName;
                }
            }
            return t.Name;
        }

        public static string GetParameterizedTemplateName(string baseName, Type t, bool applyRecursively = false, Func<Type, bool> fullName = null, Language language = Language.CSharp)
        {
            if (fullName == null)
                fullName = tt => false;

            if (!t.IsGenericType) return baseName;

            bool isVB = language == Language.VisualBasic;
            string s = baseName;
            s += isVB ? "(Of " : "<";
            bool first = true;
            foreach (var genericParameter in t.GetGenericArguments())
            {
                if (!first)
                {
                    s += ",";
                }
                if (applyRecursively && genericParameter.IsGenericType)
                {
                    s += GetParameterizedTemplateName(genericParameter, applyRecursively, language: language);
                }
                else
                {
                    s += genericParameter.FullName == null || !fullName(genericParameter)
                        ? genericParameter.Name
                        : genericParameter.FullName;
                }
                first = false;
            }
            s += isVB ? ")" : ">";
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

        private static string[] typeSeparator = new string[] { "],[" };
        public static Type[] GenericTypeArgs(string className)
        {
            var typeArgs = new List<Type>();
            var genericTypeDef = GenericTypeArgsString(className).Replace("[]", "##"); // protect array arguments
            string[] genericArgs = genericTypeDef.Split(typeSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (string genericArg in genericArgs)
            {
                string typeArg = genericArg.Trim('[', ']');
                if (typeArg.Length > 0 && typeArg != ",")
                    typeArgs.Add(Type.GetType(typeArg.Replace("##", "[]"))); // restore array arguments
            }
            return typeArgs.ToArray();
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

        public static string GetFullName(Type t, Language language = Language.CSharp)
        {
            if (t == null) throw new ArgumentNullException("t");
            if (t.IsNested && !t.IsGenericParameter)
            {
                return t.Namespace + "." + t.DeclaringType.Name + "." + GetSimpleNameHandleArray(t, language);
            }
            if (t.IsArray)
            {
                bool isVB = language == Language.VisualBasic;
                return GetFullName(t.GetElementType(), language)
                       + (isVB ? "(" : "[")
                       + new string(',', t.GetArrayRank() - 1)
                       + (isVB ? ")" : "]");
            }
            return t.FullName ?? ( t.IsGenericParameter ? GetSimpleNameHandleArray(t, language) : t.Namespace + "." + GetSimpleNameHandleArray(t, language));
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

                current = current.BaseType;
            }
        }

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

            if (!grainType.GetTypeInfo().IsAssignableFrom(type)) return false;

            // exclude generated classes.
            return !IsGeneratedType(type);
        }

        public static bool IsSystemTargetClass(Type type)
        {
            Type systemTargetType;
            if (!TryResolveType("Orleans.Runtime.SystemTarget", out systemTargetType)) return false;

            var systemTargetInterfaceType = typeof(ISystemTarget);
            var systemTargetBaseInterfaceType = typeof(ISystemTargetBase);
            if (type.Assembly.ReflectionOnly)
            {
                systemTargetType = ToReflectionOnlyType(systemTargetType);
                systemTargetInterfaceType = ToReflectionOnlyType(systemTargetInterfaceType);
                systemTargetBaseInterfaceType = ToReflectionOnlyType(systemTargetBaseInterfaceType);
            }

            if (!systemTargetInterfaceType.GetTypeInfo().IsAssignableFrom(type) ||
                !systemTargetBaseInterfaceType.GetTypeInfo().IsAssignableFrom(type) ||
                !systemTargetType.GetTypeInfo().IsAssignableFrom(type)) return false;

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
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public))
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
            return generalType.GetTypeInfo().IsAssignableFrom(type) && TypeHasAttribute(type, typeof(MethodInvokerAttribute));        
        }
        
        public static Type ResolveType(string fullName)
        {
            return CachedTypeResolver.Instance.ResolveType(fullName);
        }

        public static bool TryResolveType(string fullName, out Type type)
        {
            return CachedTypeResolver.Instance.TryResolveType(fullName, out type);            
        }

        public static Type ResolveReflectionOnlyType(string assemblyQualifiedName)
        {
            return CachedReflectionOnlyTypeResolver.Instance.ResolveType(assemblyQualifiedName);
        }

        public static Type ToReflectionOnlyType(Type type)
        {
            return type.Assembly.ReflectionOnly ? type : ResolveReflectionOnlyType(type.AssemblyQualifiedName);
        }

        public static IEnumerable<Type> GetTypes(Assembly assembly, Func<Type, bool> whereFunc)
        {
            return assembly.IsDynamic ? Enumerable.Empty<Type>() : assembly.DefinedTypes.Where(type => !type.GetTypeInfo().IsNestedPrivate && whereFunc(type));
        }

        public static IEnumerable<Type> GetTypes(Func<Type, bool> whereFunc)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var result = new List<Type>();
            foreach (var assembly in assemblies)
            {
                // there's no point in evaluating nested private types-- one of them fails to coerce to a reflection-only type anyhow.
                var types = GetTypes(assembly, whereFunc);
                result.AddRange(types);
            }
            return result;
        }

        public static IEnumerable<Type> GetTypes(List<string> assemblies, Func<Type, bool> whereFunc)
        {
            var currentAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var result = new List<Type>();
            foreach (var assembly in currentAssemblies.Where(loaded => !loaded.IsDynamic && assemblies.Contains(loaded.Location)))
            {
                // there's no point in evaluating nested private types-- one of them fails to coerce to a reflection-only type anyhow.
                var types = GetTypes(assembly, whereFunc);
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
            }

            // we can't use Type.GetCustomAttributes here because we could potentially be working with a reflection-only type.
            return CustomAttributeData.GetCustomAttributes(type).Any(
                    attrib => attribType.GetTypeInfo().IsAssignableFrom(attrib.AttributeType));
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

        /// <summary>
        /// Returns a string representation of <paramref name="type"/>.
        /// </summary>
        /// <param name="type">
        /// The type.
        /// </param>
        /// <param name="includeNamespace">
        /// A value indicating whether or not to include the namespace name.
        /// </param>
        /// <returns>
        /// A string representation of the <paramref name="type"/>.
        /// </returns>
        public static string GetParseableName(this Type type, string nameSuffix = null, bool includeNamespace = true, bool includeGenericParameters = true)
        {
            return
                ParseableNameCache.GetOrAdd(
                    Tuple.Create(type, nameSuffix, includeNamespace, includeGenericParameters),
                    _ =>
                    {
                        var builder = new StringBuilder();
                        var typeInfo = type.GetTypeInfo();
                        GetParseableName(
                            type,
                            nameSuffix ?? string.Empty,
                            builder,
                            new Queue<Type>(
                                typeInfo.IsGenericTypeDefinition ? typeInfo.GetGenericArguments() : typeInfo.GenericTypeArguments),
                            includeNamespace,
                            includeGenericParameters);
                        return builder.ToString();
                    });
        }

        /// <summary>
        /// Returns a string representation of <paramref name="type"/>.
        /// </summary>
        /// <param name="type">
        /// The type.
        /// </param>
        /// <param name="builder">
        /// The <see cref="StringBuilder"/> to append results to.
        /// </param>
        /// <param name="typeArguments">
        /// The type arguments of <paramref name="type"/>.
        /// </param>
        /// <param name="includeNamespace">
        /// A value indicating whether or not to include the namespace name.
        /// </param>
        private static void GetParseableName(
            Type type,
            string nameSuffix,
            StringBuilder builder,
            Queue<Type> typeArguments,
            bool includeNamespace = true,
            bool includeGenericParameters = true)
        {
            var typeInfo = type.GetTypeInfo();
            if (typeInfo.IsArray)
            {
                builder.AppendFormat(
                    "{0}[{1}]",
                    typeInfo.GetElementType()
                        .GetParseableName(
                            includeNamespace: includeNamespace,
                            includeGenericParameters: includeGenericParameters),
                    string.Concat(Enumerable.Range(0, type.GetArrayRank() - 1).Select(_ => ',')));
                return;
            }

            if (typeInfo.IsGenericParameter)
            {
                if (includeGenericParameters)
                {
                    builder.Append(typeInfo.GetUnadornedTypeName());
                }

                return;
            }

            if (typeInfo.DeclaringType != null)
            {
                // This is not the root type.
                GetParseableName(typeInfo.DeclaringType, string.Empty, builder, typeArguments, includeNamespace, includeGenericParameters);
                builder.Append('.');
            }
            else if (!string.IsNullOrWhiteSpace(type.Namespace) && includeNamespace)
            {
                // This is the root type.
                builder.AppendFormat("global::{0}.", type.Namespace);
            }

            if (typeInfo.IsConstructedGenericType)
            {
                // Get the unadorned name, the generic parameters, and add them together.
                var unadornedTypeName = typeInfo.GetUnadornedTypeName() + nameSuffix;
                builder.Append(EscapeIdentifier(unadornedTypeName));
                var generics =
                    Enumerable.Range(0, Math.Min(typeInfo.GetGenericArguments().Count(), typeArguments.Count))
                        .Select(_ => typeArguments.Dequeue())
                        .ToList();
                if (generics.Count > 0)
                {
                    var genericParameters = string.Join(
                        ",",
                        generics.Select(
                            generic =>
                            GetParseableName(
                                generic,
                                includeNamespace: includeNamespace,
                                includeGenericParameters: includeGenericParameters)));
                    builder.AppendFormat("<{0}>", genericParameters);
                }
            }
            else if (typeInfo.IsGenericTypeDefinition)
            {
                // Get the unadorned name, the generic parameters, and add them together.
                var unadornedTypeName = type.GetUnadornedTypeName() + nameSuffix;
                builder.Append(EscapeIdentifier(unadornedTypeName));
                var generics =
                    Enumerable.Range(0, Math.Min(type.GetGenericArguments().Count(), typeArguments.Count))
                        .Select(_ => typeArguments.Dequeue())
                        .ToList();
                if (generics.Count > 0)
                {
                    var genericParameters = string.Join(
                        ",",
                        generics.Select(_ => includeGenericParameters ? _.ToString() : string.Empty));
                    builder.AppendFormat("<{0}>", genericParameters);
                }
            }
            else
            {
                builder.Append(EscapeIdentifier(type.GetUnadornedTypeName() + nameSuffix));
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

        /// <summary>
        /// Returns the <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.
        /// </summary>
        /// <typeparam name="T">
        /// The containing type of the method.
        /// </typeparam>
        /// <param name="expression">
        /// The expression.
        /// </param>
        /// <returns>
        /// The <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.
        /// </returns>
        public static MethodInfo Method<T>(Expression<Func<T>> expression)
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
        /// <typeparam name="T">
        /// The containing type of the method.
        /// </typeparam>
        /// <param name="expression">
        /// The expression.
        /// </param>
        /// <returns>
        /// The <see cref="MethodInfo"/> for the simple method call in the provided <paramref name="expression"/>.
        /// </returns>
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
        /// Returns the namespace of the provided type, or <see cref="string.Empty"/> if the type has no namespace.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>
        /// The namespace of the provided type, or <see cref="string.Empty"/> if the type has no namespace.
        /// </returns>
        public static string GetNamespaceOrEmpty(this Type type)
        {
            if (type == null || string.IsNullOrEmpty(type.Namespace))
            {
                return string.Empty;
            }

            return type.Namespace;
        }

        /// <summary>
        /// Returns the types referenced by the provided <paramref name="type"/>.
        /// </summary>
        /// <param name="type">
        /// The type.
        /// </param>
        /// <param name="includeMethods">
        /// Whether or not to include the types referenced in the methods of this type.
        /// </param>
        /// <returns>
        /// The types referenced by the provided <paramref name="type"/>.
        /// </returns>
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
        /// Returns a value indicating whether or not the provided assembly is the Orleans assembly or references it.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>A value indicating whether or not the provided assembly is the Orleans assembly or references it.</returns>
        internal static bool IsOrleansOrReferencesOrleans(Assembly assembly)
        {
            return DoReferencesContain(assembly.GetReferencedAssemblies(), OrleansCoreAssembly)
                   || string.Equals(assembly.FullName, OrleansCoreAssembly, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns a value indicating whether or not the specified references contain the provided assembly name.
        /// </summary>
        /// <param name="references">The references.</param>
        /// <param name="assemblyName">The assembly name.</param>
        /// <returns>A value indicating whether or not the specified references contain the provided assembly name.</returns>
        private static bool DoReferencesContain(IReadOnlyCollection<AssemblyName> references, string assemblyName)
        {
            if (references.Count == 0)
            {
                return false;
            }

            return references.Any(asm => string.Equals(asm.FullName, assemblyName, StringComparison.InvariantCulture));
        }

        /// <summary>
        /// Returns the types referenced by the provided <paramref name="type"/>.
        /// </summary>
        /// <param name="type">
        /// The type.
        /// </param>
        /// <param name="includeMethods">
        /// Whether or not to include the types referenced in the methods of this type.
        /// </param>
        /// <returns>
        /// The types referenced by the provided <paramref name="type"/>.
        /// </returns>
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

            var typeInfo = type.GetTypeInfo();
            if (typeInfo.IsArray)
            {
                foreach (var elementType in type.GetElementType().GetTypes(false, exclude: exclude))
                {
                    yield return elementType;
                }
            }

            if (typeInfo.IsConstructedGenericType)
            {
                foreach (var genericTypeArgument in
                    typeInfo.GetGenericArguments().SelectMany(_ => GetTypes(_, false, exclude: exclude)))
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
