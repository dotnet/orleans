using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    [DebuggerDisplay("{MethodDescription}")]
    internal sealed class GeneratedInvokableDescription : ISerializableTypeDescription
    {
        private TypeSyntax _openTypeSyntax;
        private TypeSyntax _typeSyntax;
        private TypeSyntax _baseTypeSyntax;

        public GeneratedInvokableDescription(
            InvokableMethodDescription methodDescription,
            Accessibility accessibility,
            string generatedClassName,
            string generatedNamespaceName,
            List<IMemberDescription> members,
            List<INamedTypeSymbol> serializationHooks,
            INamedTypeSymbol baseType,
            List<TypeSyntax> constructorArguments,
            List<CompoundTypeAliasComponent[]> compoundTypeAliases,
            string returnValueInitializerMethod,
            ClassDeclarationSyntax classDeclarationSyntax)
        {
            if (methodDescription.AllTypeParameters.Count == 0)
            {
                MetadataName = $"{generatedNamespaceName}.{generatedClassName}";
            }
            else
            {
                MetadataName = $"{generatedNamespaceName}.{generatedClassName}`{methodDescription.AllTypeParameters.Count}";
            }

            BaseType = baseType;
            Name = generatedClassName;
            GeneratedNamespace = generatedNamespaceName;
            Members = members;
            MethodDescription = methodDescription;
            Accessibility = accessibility;
            SerializationHooks = serializationHooks;
            ActivatorConstructorParameters = constructorArguments;
            CompoundTypeAliases = compoundTypeAliases;
            ReturnValueInitializerMethod = returnValueInitializerMethod;
            ClassDeclarationSyntax = classDeclarationSyntax;
        }

        public Accessibility Accessibility { get; }
        public TypeSyntax TypeSyntax => _typeSyntax ??= CreateTypeSyntax();
        public TypeSyntax OpenTypeSyntax => _openTypeSyntax ??= CreateOpenTypeSyntax();
        public bool HasComplexBaseType => BaseType is { SpecialType: not SpecialType.System_Object };
        public bool IncludePrimaryConstructorParameters => false;
        public INamedTypeSymbol BaseType { get; }
        public TypeSyntax BaseTypeSyntax => _baseTypeSyntax ??= BaseType.ToTypeSyntax(MethodDescription.TypeParameterSubstitutions);
        public string Namespace => GeneratedNamespace;
        public string GeneratedNamespace { get; }
        public string Name { get; }
        public bool IsValueType => false;
        public bool IsSealedType => true;
        public bool IsAbstractType => false;
        public bool IsEnumType => false;
        public bool IsGenericType => TypeParameters.Count > 0;
        public List<IMemberDescription> Members { get; }
        public Compilation Compilation => MethodDescription.CodeGenerator.Compilation;
        public bool IsEmptyConstructable => ActivatorConstructorParameters is not { Count: > 0 };
        public bool UseActivator => ActivatorConstructorParameters is { Count: > 0 };
        public bool TrackReferences => false;
        public bool OmitDefaultMemberValues => false;
        public List<(string Name, ITypeParameterSymbol Parameter)> TypeParameters => MethodDescription.AllTypeParameters;
        public List<INamedTypeSymbol> SerializationHooks { get; }
        public bool IsShallowCopyable => false;
        public bool IsUnsealedImmutable => false;
        public bool IsImmutable => false;
        public bool IsExceptionType => false;
        public List<TypeSyntax> ActivatorConstructorParameters { get; }
        public bool HasActivatorConstructor => UseActivator;
        public List<CompoundTypeAliasComponent[]> CompoundTypeAliases { get; }
        public ClassDeclarationSyntax ClassDeclarationSyntax { get; }
        public string ReturnValueInitializerMethod { get; }

        public InvokableMethodDescription MethodDescription { get; }
        public string MetadataName { get; }

        public ExpressionSyntax GetObjectCreationExpression() => ObjectCreationExpression(TypeSyntax, ArgumentList(), null);

        private TypeSyntax CreateTypeSyntax()
        {
            var simpleName = InvokableGenerator.GetSimpleClassName(MethodDescription);
            var subs = MethodDescription.TypeParameterSubstitutions;
            return (TypeParameters, Namespace) switch
            {
                ({ Count: > 0 }, { Length: > 0 }) => QualifiedName(ParseName(Namespace), GenericName(Identifier(simpleName), TypeArgumentList(SeparatedList<TypeSyntax>(TypeParameters.Select(p => IdentifierName(subs[p.Parameter])))))),
                ({ Count: > 0 }, _) => GenericName(Identifier(simpleName), TypeArgumentList(SeparatedList<TypeSyntax>(TypeParameters.Select(p => IdentifierName(subs[p.Parameter]))))),
                (_, { Length: > 0 }) => QualifiedName(ParseName(Namespace), IdentifierName(simpleName)),
                _ => IdentifierName(simpleName),
            };
        }

        private TypeSyntax CreateOpenTypeSyntax()
        {
            var simpleName = InvokableGenerator.GetSimpleClassName(MethodDescription);
            return (TypeParameters, Namespace) switch
            {
                ({ Count: > 0 }, { Length: > 0 }) => QualifiedName(ParseName(Namespace), GenericName(Identifier(simpleName), TypeArgumentList(SeparatedList<TypeSyntax>(TypeParameters.Select(p => OmittedTypeArgument()))))),
                ({ Count: > 0 }, _) => GenericName(Identifier(simpleName), TypeArgumentList(SeparatedList<TypeSyntax>(TypeParameters.Select(p => OmittedTypeArgument())))),
                (_, { Length: > 0 }) => QualifiedName(ParseName(Namespace), IdentifierName(simpleName)),
                _ => IdentifierName(simpleName),
            };
        }
    }
}