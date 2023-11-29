using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.SyntaxGeneration;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Describes an invokable method.
    /// This is a method on the original interface which defined it.
    /// By contrast, <see cref="ProxyMethodDescription"/> describes a method on an interface which a proxy is being generated for, having type argument substitutions, etc.
    /// </summary>
    internal sealed class InvokableMethodDescription : IEquatable<InvokableMethodDescription>
    {
        public static InvokableMethodDescription Create(InvokableMethodId method, INamedTypeSymbol containingType = null) => new(method, containingType);

        private InvokableMethodDescription(InvokableMethodId invokableId, INamedTypeSymbol containingType)
        {
            Key = invokableId;
            ContainingInterface = containingType ?? invokableId.Method.ContainingType;
            GeneratedMethodId = CodeGenerator.CreateHashedMethodId(Method);
            MethodId = CodeGenerator.GetId(Method)?.ToString(CultureInfo.InvariantCulture) ?? CodeGenerator.GetAlias(Method) ?? GeneratedMethodId;

            MethodTypeParameters = new List<(string Name, ITypeParameterSymbol Parameter)>();

            // Set defaults from the interface type.
            var invokableBaseTypes = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var pair in ProxyBase.InvokableBaseTypes)
            {
                invokableBaseTypes[pair.Key] = pair.Value;
            }

            InvokableBaseTypes = invokableBaseTypes;
            foreach (var methodAttr in Method.GetAttributes())
            {
                if (methodAttr.AttributeClass.GetAttributes(CodeGenerator.LibraryTypes.InvokableBaseTypeAttribute, out var attrs))
                {
                    foreach (var attr in attrs)
                    {
                        var ctorArgs = attr.ConstructorArguments;
                        var proxyBaseType = (INamedTypeSymbol)ctorArgs[0].Value;
                        var returnType = (INamedTypeSymbol)ctorArgs[1].Value;
                        var invokableBaseType = (INamedTypeSymbol)ctorArgs[2].Value;
                        if (!SymbolEqualityComparer.Default.Equals(ProxyBase.ProxyBaseType, proxyBaseType))
                        {
                            // This attribute does not apply to this particular invoker, since it is for a different proxy base type.
                            continue;
                        }

                        invokableBaseTypes[returnType] = invokableBaseType;
                    }
                }

                if (methodAttr.AttributeClass.GetAttributes(CodeGenerator.LibraryTypes.InvokableCustomInitializerAttribute, out attrs))
                {
                    foreach (var attr in attrs)
                    {
                        var methodName = (string)attr.ConstructorArguments[0].Value;

                        TypedConstant methodArgument;
                        if (attr.ConstructorArguments.Length == 2)
                        {
                            // Take the value from the attribute directly.
                            methodArgument = attr.ConstructorArguments[1];
                        }
                        else
                        {
                            // Take the value from the attribute which this attribute is attached to.
                            if (TryGetNamedArgument(attr.NamedArguments, "AttributeArgumentName", out var argNameArg)
                                && TryGetNamedArgument(methodAttr.NamedArguments, (string)argNameArg.Value, out var namedArgument))
                            {
                                methodArgument = namedArgument;
                            }
                            else
                            {
                                var index = 0;
                                if (TryGetNamedArgument(attr.NamedArguments, "AttributeArgumentIndex", out var indexArg))
                                {
                                    index = (int)indexArg.Value;
                                }

                                methodArgument = methodAttr.ConstructorArguments[index];
                            }
                        }

                        CustomInitializerMethods.Add((methodName, methodArgument));
                    }
                }

                if (SymbolEqualityComparer.Default.Equals(methodAttr.AttributeClass, CodeGenerator.LibraryTypes.ResponseTimeoutAttribute))
                {
                    ResponseTimeoutTicks = TimeSpan.Parse((string)methodAttr.ConstructorArguments[0].Value).Ticks;
                }
            }

            AllTypeParameters = new List<(string Name, ITypeParameterSymbol Parameter)>();
            MethodTypeParameters = new List<(string Name, ITypeParameterSymbol Parameter)>();

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var typeParameter in ContainingInterface.GetAllTypeParameters())
            {
                var tpName = GetTypeParameterName(names, typeParameter);
                AllTypeParameters.Add((tpName, typeParameter));
            }

            foreach (var typeParameter in Method.TypeParameters)
            {
                var tpName = GetTypeParameterName(names, typeParameter);
                AllTypeParameters.Add((tpName, typeParameter));
                MethodTypeParameters.Add((tpName, typeParameter));
            }

            TypeParameterSubstitutions = new(SymbolEqualityComparer.Default);
            foreach (var (name, parameter) in AllTypeParameters)
            {
                TypeParameterSubstitutions[parameter] = name;
            }

            static string GetTypeParameterName(HashSet<string> names, ITypeParameterSymbol typeParameter)
            {
                var count = 0;
                var result = typeParameter.Name;
                while (names.Contains(result))
                {
                    result = $"{typeParameter.Name}_{++count}";
                }

                names.Add(result);
                return result.EscapeIdentifier();
            }

            static bool TryGetNamedArgument(ImmutableArray<KeyValuePair<string, TypedConstant>> arguments, string name, out TypedConstant value)
            {
                foreach (var arg in arguments)
                {
                    if (string.Equals(arg.Key, name, StringComparison.Ordinal))
                    {
                        value = arg.Value;
                        return true;
                    }
                }

                value = default;
                return false;
            }
        }

        /// <summary>
        /// Gets the source generator.
        /// </summary>
        public CodeGenerator CodeGenerator => ProxyBase.CodeGenerator;

        /// <summary>
        /// Gets the method identifier.
        /// </summary>
        public InvokableMethodId Key { get; }

        /// <summary>
        /// Gets the proxy base information for the method (eg, GrainReference, whether it is an extension).
        /// </summary>
        public InvokableMethodProxyBase ProxyBase => Key.ProxyBase;

        /// <summary>
        /// Gets the method symbol.
        /// </summary>
        public IMethodSymbol Method => Key.Method;

        /// <summary>
        /// Gets the dictionary of invokable base types. This indicates what invokable base type (eg, ValueTaskRequest) should be used for a given return type (eg, ValueTask).
        /// </summary>
        public IReadOnlyDictionary<INamedTypeSymbol, INamedTypeSymbol> InvokableBaseTypes { get; }

        /// <summary>
        /// Gets the response timeout ticks, if set.
        /// </summary>
        public long? ResponseTimeoutTicks { get; }

        /// <summary>
        /// Gets the list of custom initializer method names and their corresponding argument.
        /// </summary>
        public List<(string MethodName, TypedConstant MethodArgument)> CustomInitializerMethods { get; } = new();

        /// <summary>
        /// Gets the generated method identifier.
        /// </summary>
        public string GeneratedMethodId { get; }

        /// <summary>
        /// Gets the method identifier.
        /// </summary>
        public string MethodId { get; }

        public List<(string Name, ITypeParameterSymbol Parameter)> AllTypeParameters { get; }
        public List<(string Name, ITypeParameterSymbol Parameter)> MethodTypeParameters { get; }
        public Dictionary<ITypeParameterSymbol, string> TypeParameterSubstitutions { get; }

        /// <summary>
        /// Gets a value indicating whether this method has an alias.
        /// </summary>
        public bool HasAlias => !string.Equals(MethodId, GeneratedMethodId, StringComparison.Ordinal);

        /// <summary>
        /// Gets the interface which this type is contained in.
        /// </summary>
        public INamedTypeSymbol ContainingInterface { get; }

        public bool Equals(InvokableMethodDescription other) => Key.Equals(other.Key);
        public override bool Equals(object obj) => obj is InvokableMethodDescription imd && Equals(imd);
        public override int GetHashCode() => Key.GetHashCode();
        public override string ToString() => $"{ProxyBase}/{Method.ContainingType.Name}/{Method.Name}";
    }
}