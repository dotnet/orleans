using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Hashing;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.SyntaxGeneration.SymbolExtensions;

#nullable disable
namespace Orleans.CodeGenerator
{
    public class CodeGeneratorOptions
    {
        public const string IdAttribute = "Orleans.IdAttribute";
        public const string AliasAttribute = "Orleans.AliasAttribute";
        public const string ImmutableAttribute = "Orleans.ImmutableAttribute";
        public static readonly IReadOnlyList<string> ConstructorAttributes = ["Orleans.OrleansConstructorAttribute", "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute"];
        public GenerateFieldIds GenerateFieldIds { get; set; }
        public bool GenerateCompatibilityInvokers { get; set; }
    }

    public class CodeGenerator : IGeneratorServices
    {
        internal const string CodeGeneratorName = "OrleansCodeGen";
        private readonly Dictionary<string, List<MemberDeclarationSyntax>> _namespacedMembers = new();
        private readonly Dictionary<InvokableMethodId, InvokableMethodDescription> _invokableMethodDescriptions = new();
        private readonly HashSet<INamedTypeSymbol> _visitedInterfaces = new(SymbolEqualityComparer.Default);

        public CodeGenerator(Compilation compilation, CodeGeneratorOptions options)
            : this(compilation, options, LibraryTypes.FromCompilation(compilation, options))
        {
        }

        internal CodeGenerator(Compilation compilation, CodeGeneratorOptions options, LibraryTypes libraryTypes)
        {
            Compilation = compilation;
            Options = options;
            LibraryTypes = libraryTypes;
            MetadataModel = new MetadataModel();
            ProxyGenerator = new ProxyGenerator(this, new CopierGenerator(this));
            InvokableGenerator = new InvokableGenerator(this);
        }

        public Compilation Compilation { get; }
        public CodeGeneratorOptions Options { get; }
        internal LibraryTypes LibraryTypes { get; }
        LibraryTypes IGeneratorServices.LibraryTypes => LibraryTypes;
        internal MetadataModel MetadataModel { get; }
        internal ProxyGenerator ProxyGenerator { get; }
        internal InvokableGenerator InvokableGenerator { get; }
        public static string GetGeneratedNamespaceName(ITypeSymbol type) => type.GetNamespaceAndNesting() switch
        {
            { Length: > 0 } ns => $"{CodeGeneratorName}.{ns}",
            _ => CodeGeneratorName
        };

        public void AddMember(string ns, MemberDeclarationSyntax member)
        {
            if (!_namespacedMembers.TryGetValue(ns, out var existing))
            {
                existing = _namespacedMembers[ns] = new List<MemberDeclarationSyntax>();
            }

            existing.Add(member);
        }

        internal IEnumerable<(string Namespace, MemberDeclarationSyntax Member)> GetEmittedMembers()
        {
            foreach (var entry in _namespacedMembers)
            {
                foreach (var member in entry.Value)
                {
                    yield return (entry.Key, member);
                }
            }
        }

        public uint? GetId(ISymbol memberSymbol) => GetId(LibraryTypes, memberSymbol);

        internal static uint? GetId(LibraryTypes libraryTypes, ISymbol memberSymbol)
        {
            return memberSymbol.GetAttribute(libraryTypes.IdAttributeType) is { } attr
                ? (uint)attr.ConstructorArguments.First().Value
                : null;
        }

        internal static string CreateHashedMethodId(IMethodSymbol methodSymbol)
        {
            var methodSignature = Format(methodSymbol);
            var hash = XxHash32.Hash(Encoding.UTF8.GetBytes(methodSignature));
            return $"{HexConverter.ToString(hash)}";

            static string Format(IMethodSymbol methodInfo)
            {
                var result = new StringBuilder();
                result.Append(methodInfo.ContainingType.ToDisplayName());
                result.Append('.');
                result.Append(methodInfo.Name);

                if (methodInfo.IsGenericMethod)
                {
                    result.Append('<');
                    var first = true;
                    foreach (var typeArgument in methodInfo.TypeArguments)
                    {
                        if (!first) result.Append(',');
                        else first = false;
                        result.Append(typeArgument.Name);
                    }

                    result.Append('>');
                }

                {
                    result.Append('(');
                    var parameters = methodInfo.Parameters;
                    var first = true;
                    foreach (var parameter in parameters)
                    {
                        if (!first)
                        {
                            result.Append(',');
                        }

                        var parameterType = parameter.Type;
                        switch (parameterType)
                        {
                            case ITypeParameterSymbol _:
                                result.Append(parameterType.Name);
                                break;
                            default:
                                result.Append(parameterType.ToDisplayName());
                                break;
                        }

                        first = false;
                    }
                }

                result.Append(')');
                return result.ToString();
            }
        }
        public string GetAlias(ISymbol symbol) => (string)symbol.GetAttribute(LibraryTypes.AliasAttribute)?.ConstructorArguments.First().Value;
        internal static AttributeListSyntax GetGeneratedCodeAttributes() => GeneratedCodeAttributeSyntax;
        private static readonly AttributeListSyntax GeneratedCodeAttributeSyntax =
            AttributeList().AddAttributes(
                Attribute(ParseName("global::System.CodeDom.Compiler.GeneratedCodeAttribute"))
                    .AddArgumentListArguments(
                        AttributeArgument(CodeGeneratorName.GetLiteralExpression()),
                        AttributeArgument(typeof(CodeGenerator).Assembly.GetName().Version.ToString().GetLiteralExpression())),
                Attribute(ParseName("global::System.ComponentModel.EditorBrowsableAttribute"))
                    .AddArgumentListArguments(
                        AttributeArgument(ParseName("global::System.ComponentModel.EditorBrowsableState").Member("Never"))),
                        Attribute(ParseName("global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute"))
            );

        internal static AttributeSyntax GetMethodImplAttributeSyntax() => MethodImplAttributeSyntax;
        private static readonly AttributeSyntax MethodImplAttributeSyntax =
            Attribute(ParseName("global::System.Runtime.CompilerServices.MethodImplAttribute"))
                .AddArgumentListArguments(AttributeArgument(ParseName("global::System.Runtime.CompilerServices.MethodImplOptions").Member("AggressiveInlining")));

        internal void VisitInterface(INamedTypeSymbol interfaceType)
        {
            // Get or generate an invokable for the original method definition.
            if (!SymbolEqualityComparer.Default.Equals(interfaceType, interfaceType.OriginalDefinition))
            {
                interfaceType = interfaceType.OriginalDefinition;
            }

            if (!_visitedInterfaces.Add(interfaceType))
            {
                return;
            }

            foreach (var proxyBase in GetProxyBases(interfaceType))
            {
                _ = GetInvokableInterfaceDescription(proxyBase.ProxyBaseType, interfaceType);
            }

            /*
            foreach (var baseInterface in interfaceType.AllInterfaces)
            {
                VisitInterface(baseInterface);
            }
            */
        }

        internal bool TryGetInvokableInterfaceDescription(INamedTypeSymbol interfaceType, out ProxyInterfaceDescription result)
        {
            if (!TryGetProxyBaseDescription(interfaceType, out var description))
            {
                result = null;
                return false;
            }

            result = GetInvokableInterfaceDescription(description.ProxyBaseType, interfaceType);
            return true;
        }

        private readonly Dictionary<INamedTypeSymbol, List<InvokableMethodProxyBase>> _interfaceProxyBases = new(SymbolEqualityComparer.Default);
        internal List<InvokableMethodProxyBase> GetProxyBases(INamedTypeSymbol interfaceType)
        {
            if (_interfaceProxyBases.TryGetValue(interfaceType, out var result))
            {
                return result;
            }

            result = new List<InvokableMethodProxyBase>();
            if (interfaceType.GetAttributes(LibraryTypes.GenerateMethodSerializersAttribute, out var attributes, inherited: true))
            {
                foreach (var attribute in attributes)
                {
                    var proxyBase = GetProxyBaseDescription(attribute);
                    if (!result.Contains(proxyBase))
                    {
                        result.Add(proxyBase);
                    }
                }
            }

            return result;
        }

        internal bool TryGetProxyBaseDescription(INamedTypeSymbol interfaceType, out InvokableMethodProxyBase result)
        {
            var attribute = interfaceType.GetAttribute(LibraryTypes.GenerateMethodSerializersAttribute, inherited: true);
            if (attribute == null)
            {
                result = null;
                return false;
            }

            result = GetProxyBaseDescription(attribute);
            return true;
        }

        private InvokableMethodProxyBase GetProxyBaseDescription(AttributeData attribute)
        {
            var proxyBaseType = ((INamedTypeSymbol)attribute.ConstructorArguments[0].Value).OriginalDefinition;
            var isExtension = (bool)attribute.ConstructorArguments[1].Value;
            var invokableBaseTypes = GetInvokableBaseTypes(proxyBaseType);
            var descriptor = new InvokableMethodProxyBaseId(proxyBaseType, isExtension);
            var description = new InvokableMethodProxyBase(this, descriptor, invokableBaseTypes);
            return description;

            Dictionary<INamedTypeSymbol, INamedTypeSymbol> GetInvokableBaseTypes(INamedTypeSymbol baseClass)
            {
                // Set the base invokable types which are used if attributes on individual methods do not override them.
                if (!MetadataModel.ProxyBaseTypeInvokableBaseTypes.TryGetValue(baseClass, out var invokableBaseTypes))
                {
                    invokableBaseTypes = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
                    if (baseClass.GetAttributes(LibraryTypes.DefaultInvokableBaseTypeAttribute, out var invokableBaseTypeAttributes))
                    {
                        foreach (var attr in invokableBaseTypeAttributes)
                        {
                            var ctorArgs = attr.ConstructorArguments;
                            var returnType = (INamedTypeSymbol)ctorArgs[0].Value;
                            var invokableBaseType = (INamedTypeSymbol)ctorArgs[1].Value;
                            invokableBaseTypes[returnType] = invokableBaseType;
                        }
                    }

                    MetadataModel.ProxyBaseTypeInvokableBaseTypes[baseClass] = invokableBaseTypes;
                }

                return invokableBaseTypes;
            }
        }

        internal InvokableMethodProxyBase GetProxyBase(INamedTypeSymbol interfaceType)
        {
            if (!TryGetProxyBaseDescription(interfaceType, out var result))
            {
                throw new InvalidOperationException($"Cannot get proxy base description for a type which does not have or inherit [{nameof(LibraryTypes.GenerateMethodSerializersAttribute)}]");
            }

            return result;
        }

        private ProxyInterfaceDescription GetInvokableInterfaceDescription(INamedTypeSymbol proxyBaseType, INamedTypeSymbol interfaceType)
        {
            var originalInterface = interfaceType.OriginalDefinition;
            if (MetadataModel.InvokableInterfaces.TryGetValue(originalInterface, out var description))
            {
                return description;
            }

            description = new ProxyInterfaceDescription(this, proxyBaseType, originalInterface);
            MetadataModel.InvokableInterfaces.Add(originalInterface, description);

            // Generate a proxy.
            var (generatedClass, proxyDescription) = ProxyGenerator.Generate(description);

            // Emit the generated proxy
            if (Compilation.GetTypeByMetadataName(proxyDescription.MetadataName) == null)
            {
                AddMember(proxyDescription.InterfaceDescription.GeneratedNamespace, generatedClass);
            }

            return description;
        }

        internal ProxyMethodDescription GetProxyMethodDescription(INamedTypeSymbol interfaceType, IMethodSymbol method)
        {
            var originalMethod = method.OriginalDefinition;
            var proxyBaseInfo = GetProxyBase(interfaceType);

            // For extensions, we want to ensure that the containing type is always the extension.
            // This ensures that we will always know which 'component' to get in our SetTarget method.
            // If the type is not an extension, use the original method definition's containing type.
            // This is the interface where the type was originally defined.
            var containingType = proxyBaseInfo.IsExtension ? interfaceType : originalMethod.ContainingType;

            var invokableId = new InvokableMethodId(proxyBaseInfo, containingType, originalMethod);
            var interfaceDescription = GetInvokableInterfaceDescription(invokableId.ProxyBase.ProxyBaseType, interfaceType);

            // Get or generate an invokable for the original method definition.
            if (!MetadataModel.GeneratedInvokables.TryGetValue(invokableId, out var generatedInvokable))
            {
                if (!_invokableMethodDescriptions.TryGetValue(invokableId, out var methodDescription))
                {
                    methodDescription = _invokableMethodDescriptions[invokableId] = InvokableMethodDescription.Create(invokableId, containingType);
                }

                generatedInvokable = MetadataModel.GeneratedInvokables[invokableId] = InvokableGenerator.Generate(methodDescription);

                if (Compilation.GetTypeByMetadataName(generatedInvokable.MetadataName) == null)
                {
                    // Emit the generated code on-demand.
                    AddMember(generatedInvokable.GeneratedNamespace, generatedInvokable.ClassDeclarationSyntax);
                }
            }

            var proxyMethodDescription = ProxyMethodDescription.Create(interfaceDescription, generatedInvokable, method);

            // For backwards compatibility, generate invokers for the specific implementation types as well, where they differ.
            if (Options.GenerateCompatibilityInvokers && !SymbolEqualityComparer.Default.Equals(method.OriginalDefinition.ContainingType, interfaceType))
            {
                var compatInvokableId = new InvokableMethodId(proxyBaseInfo, interfaceType, method);
                var compatMethodDescription = InvokableMethodDescription.Create(compatInvokableId, interfaceType);
                var compatInvokable = InvokableGenerator.Generate(compatMethodDescription);
                AddMember(compatInvokable.GeneratedNamespace, compatInvokable.ClassDeclarationSyntax);
            }

            return proxyMethodDescription;
        }
    }
}
