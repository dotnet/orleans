using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// A collection of utility functions for dealing with Type information.
    /// </summary>
    internal static class TypeUtils
    {
        private static readonly ConcurrentDictionary<Tuple<Type, TypeFormattingOptions>, string> ParseableNameCache = new ConcurrentDictionary<Tuple<Type, TypeFormattingOptions>, string>();

        public static string GetSimpleTypeName(Type type, Predicate<Type> fullName = null)
        {
            if (type.IsNestedPublic || type.IsNestedPrivate)
            {
                if (type.DeclaringType.IsGenericType)
                {
                    return GetTemplatedName(
                        GetUntemplatedTypeName(type.DeclaringType.Name),
                        type.DeclaringType,
                        type.GetGenericArgumentsSafe(),
                        _ => true) + "." + GetUntemplatedTypeName(type.Name);
                }

                return GetTemplatedName(type.DeclaringType) + "." + GetUntemplatedTypeName(type.Name);
            }

            if (type.IsGenericType) return GetSimpleTypeName(fullName != null && fullName(type) ? GetFullName(type) : type.Name);

            return fullName != null && fullName(type) ? GetFullName(type) : type.Name;
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

        public static string GetTemplatedName(Type t, Predicate<Type> fullName = null)
        {
            if (fullName == null)
                fullName = _ => true; // default to full type names

            if (t.IsGenericType) return GetTemplatedName(GetSimpleTypeName(t, fullName), t, t.GetGenericArgumentsSafe(), fullName);

            if (t.IsArray)
            {
                return GetTemplatedName(t.GetElementType(), fullName)
                       + "["
                       + new string(',', t.GetArrayRank() - 1)
                       + "]";
            }

            return GetSimpleTypeName(t, fullName);
        }

        public static string GetTemplatedName(string baseName, Type t, Type[] genericArguments, Predicate<Type> fullName)
        {
            if (!t.IsGenericType || (t.DeclaringType != null && t.DeclaringType.IsGenericType)) return baseName;
            string s = baseName;
            s += "<";
            s += GetGenericTypeArgs(genericArguments, fullName);
            s += ">";
            return s;
        }

        public static Type[] GetGenericArgumentsSafe(this Type type)
        {
            var result = type.GetGenericArguments();

            if (type.ContainsGenericParameters)
            {
                // Get generic parameter from generic type definition to have consistent naming for inherited interfaces
                // Example: interface IA<TName>, class A<TOtherName>: IA<OtherName>
                // in this case generic parameter name of IA interface from class A is OtherName instead of TName.
                // To avoid this situation use generic parameter from generic type definition.
                // Matching by position in array, because GenericParameterPosition is number across generic parameters.
                // For half open generic types (IA<int,T>) T will have position 0.
                var originalGenericArguments = type.GetGenericTypeDefinition().GetGenericArguments();
                if (result.Length != originalGenericArguments.Length) // this check may be redunant
                    return result;

                for (int i = 0; i < result.Length; i++)
                {
                    if (result[i].IsGenericParameter)
                        result[i] = originalGenericArguments[i];
                }
            }
            return result;
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

                if (!genericParameter.IsGenericType)
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

        public static string GetParameterizedTemplateName(Type type, Predicate<Type> fullName = null, bool applyRecursively = false)
        {
            if (fullName == null)
                fullName = tt => true;

            if (type.IsGenericType)
            {
                return GetParameterizedTemplateName(GetSimpleTypeName(type, fullName), type, applyRecursively, fullName);
            }

            if (fullName != null && fullName(type) == true)
            {
                return type.FullName;
            }

            return type.Name;
        }

        public static string GetParameterizedTemplateName(string baseName, Type type, bool applyRecursively = false, Predicate<Type> fullName = null)
        {
            if (fullName == null)
                fullName = tt => false;

            if (!type.IsGenericType) return baseName;

            string s = baseName;
            s += "<";
            bool first = true;
            foreach (var genericParameter in type.GetGenericArguments())
            {
                if (!first)
                {
                    s += ",";
                }
                if (applyRecursively && genericParameter.IsGenericType)
                {
                    s += GetParameterizedTemplateName(genericParameter, fullName: null, applyRecursively: applyRecursively);
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
            return t.IsGenericType ? baseName + '`' + t.GetGenericArguments().Length : baseName;
        }

        public static string GetRawClassName(string typeName)
        {
            int i = typeName.IndexOf('[');
            return i <= 0 ? typeName : typeName.Substring(0, i);
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

            // using of t.FullName breaks interop with core and full .net in one cluster, because
            // FullName of types from corelib is different.
            // .net core int: [System.Int32, System.Private.CoreLib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]
            // full .net int: [System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]
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

                current = current.BaseType;
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

            if (grainType == type || grainChevronType == type) return false;

            if (!grainType.IsAssignableFrom(type)) return false;

            // exclude generated classes.
            return !type.IsDefined(typeof(GeneratedCodeAttribute), false);
        }

        public static bool IsConcreteGrainClass(Type type, out IEnumerable<string> complaints, bool complain)
        {
            complaints = null;
            if (!IsGrainClass(type)) return false;
            if (!type.IsAbstract) return true;

            complaints = complain ? new[] { string.Format("Grain type {0} is abstract and cannot be instantiated.", type.FullName) } : null;
            return false;
        }

        public static bool IsConcreteGrainClass(Type type, out IEnumerable<string> complaints)
        {
            return IsConcreteGrainClass(type, out complaints, complain: true);
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

            return methodInfo.DeclaringType.IsInterface
                   && typeof(IAddressable).IsAssignableFrom(methodInfo.DeclaringType);
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

        /// <summary>Returns a string representation of <paramref name="type"/>.</summary>
        /// <param name="type">The type.</param>
        /// <param name="options">The type formatting options.</param>
        /// <param name="getNameFunc">The delegate used to get the unadorned, simple type name of <paramref name="type"/>.</param>
        /// <returns>A string representation of the <paramref name="type"/>.</returns>
        public static string GetParseableName(this Type type, TypeFormattingOptions options = null, Func<Type, string> getNameFunc = null)
        {
            options = options ?? TypeFormattingOptions.Default;

            // If a naming function has been specified, skip the cache.
            if (getNameFunc != null) return BuildParseableName();

            return ParseableNameCache.GetOrAdd(Tuple.Create(type, options), _ => BuildParseableName());

            string BuildParseableName()
            {
                var builder = new StringBuilder();
                GetParseableName(
                    type,
                    builder,
                    new Queue<Type>(
                        type.IsGenericTypeDefinition
                            ? type.GetGenericArguments()
                            : type.GenericTypeArguments),
                    options,
                    getNameFunc ?? (t => t.GetUnadornedTypeName() + options.NameSuffix));
                return builder.ToString();
            }
        }

        /// <summary>Returns a string representation of <paramref name="type"/>.</summary>
        /// <param name="type">The type.</param>
        /// <param name="builder">The <see cref="StringBuilder"/> to append results to.</param>
        /// <param name="typeArguments">The type arguments of <paramref name="type"/>.</param>
        /// <param name="options">The type formatting options.</param>
        /// <param name="getNameFunc">Delegate that returns name for a type.</param>
        private static void GetParseableName(
            Type type,
            StringBuilder builder,
            Queue<Type> typeArguments,
            TypeFormattingOptions options,
            Func<Type, string> getNameFunc)
        {
            if (type.IsArray)
            {
                var elementType = type.GetElementType().GetParseableName(options);
                if (!string.IsNullOrWhiteSpace(elementType))
                {
                    builder.AppendFormat(
                        "{0}[{1}]",
                        elementType,
                        new string(',', type.GetArrayRank() - 1));
                }

                return;
            }

            if (type.IsGenericParameter)
            {
                if (options.IncludeGenericTypeParameters)
                {
                    builder.Append(type.GetUnadornedTypeName());
                }

                return;
            }

            if (type.DeclaringType != null)
            {
                // This is not the root type.
                GetParseableName(type.DeclaringType, builder, typeArguments, options, t => t.GetUnadornedTypeName());
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
                var unadornedTypeName = getNameFunc(type);
                builder.Append(EscapeIdentifier(unadornedTypeName));
                var generics =
                    Enumerable.Range(0, Math.Min(type.GetGenericArguments().Length, typeArguments.Count))
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
            else if (type.IsGenericTypeDefinition)
            {
                // Get the unadorned name, the generic parameters, and add them together.
                var unadornedTypeName = getNameFunc(type);
                builder.Append(EscapeIdentifier(unadornedTypeName));
                var generics =
                    Enumerable.Range(0, Math.Min(type.GetGenericArguments().Length, typeArguments.Count))
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
                builder.Append(EscapeIdentifier(getNameFunc(type)));
            }
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

        private static string EscapeIdentifier(string identifier)
        {
            if (IsCSharpKeyword(identifier)) return "@" + identifier;
            return identifier;
        }

        internal static bool IsCSharpKeyword(string identifier)
        {
            switch (identifier)
            {
                case "abstract":
                case "add":
                case "alias":
                case "as":
                case "ascending":
                case "async":
                case "await":
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
                case "descending":
                case "do":
                case "double":
                case "dynamic":
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
                case "from":
                case "get":
                case "global":
                case "goto":
                case "group":
                case "if":
                case "implicit":
                case "in":
                case "int":
                case "interface":
                case "internal":
                case "into":
                case "is":
                case "join":
                case "let":
                case "lock":
                case "long":
                case "nameof":
                case "namespace":
                case "new":
                case "null":
                case "object":
                case "operator":
                case "orderby":
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
                case "select":
                case "set":
                case "short":
                case "sizeof":
                case "stackalloc":
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
                case "unchecked":
                case "unsafe":
                case "ushort":
                case "using":
                case "value":
                case "var":
                case "virtual":
                case "void":
                case "volatile":
                case "when":
                case "where":
                case "while":
                case "yield":
                    return true;
                default:
                    return false;
            }
        }
    }
}
