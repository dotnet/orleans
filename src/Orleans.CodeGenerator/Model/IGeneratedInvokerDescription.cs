using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal sealed class GeneratedInvokerDescription : ISerializableTypeDescription
    {
        private readonly MethodDescription _methodDescription;
        private TypeSyntax _openTypeSyntax;
        private TypeSyntax _typeSyntax;
        private TypeSyntax _baseTypeSyntax;

        public GeneratedInvokerDescription(
            InvokableInterfaceDescription interfaceDescription,
            MethodDescription methodDescription,
            Accessibility accessibility,
            string generatedClassName,
            List<IMemberDescription> members,
            List<INamedTypeSymbol> serializationHooks,
            INamedTypeSymbol baseType,
            List<TypeSyntax> constructorArguments,
            CompoundTypeAliasComponent[] compoundTypeAliasArguments,
            string returnValueInitializerMethod)
        {
            InterfaceDescription = interfaceDescription;
            _methodDescription = methodDescription;
            BaseType = baseType;
            Name = generatedClassName;
            Members = members;

            Accessibility = accessibility;
            SerializationHooks = serializationHooks;
            ActivatorConstructorParameters = constructorArguments;
            CompoundTypeAliasArguments = compoundTypeAliasArguments;
            ReturnValueInitializerMethod = returnValueInitializerMethod;
        }

        public Accessibility Accessibility { get; }
        public TypeSyntax TypeSyntax => _typeSyntax ??= CreateTypeSyntax();
        public TypeSyntax OpenTypeSyntax => _openTypeSyntax ??= CreateOpenTypeSyntax();
        public bool HasComplexBaseType => BaseType is { SpecialType: not SpecialType.System_Object };
        public bool IncludePrimaryConstructorParameters => false;
        public INamedTypeSymbol BaseType { get; }
        public TypeSyntax BaseTypeSyntax => _baseTypeSyntax ??= BaseType.ToTypeSyntax(_methodDescription.TypeParameterSubstitutions);
        public string Namespace => GeneratedNamespace;
        public string GeneratedNamespace => InterfaceDescription.GeneratedNamespace;
        public string Name { get; }
        public bool IsValueType => false;
        public bool IsSealedType => true;
        public bool IsAbstractType => false;
        public bool IsEnumType => false;
        public bool IsGenericType => TypeParameters.Count > 0;
        public List<IMemberDescription> Members { get; }
        public InvokableInterfaceDescription InterfaceDescription { get; }
        public SemanticModel SemanticModel => InterfaceDescription.SemanticModel;
        public bool IsEmptyConstructable => ActivatorConstructorParameters is not { Count: > 0 };
        public bool UseActivator => ActivatorConstructorParameters is { Count: > 0 };
        public bool TrackReferences => false;
        public bool OmitDefaultMemberValues => false;
        public List<(string Name, ITypeParameterSymbol Parameter)> TypeParameters => _methodDescription.AllTypeParameters;
        public List<INamedTypeSymbol> SerializationHooks { get; }
        public bool IsShallowCopyable => false;
        public bool IsUnsealedImmutable => false;
        public bool IsImmutable => false;
        public bool IsExceptionType => false;
        public List<TypeSyntax> ActivatorConstructorParameters { get; }
        public bool HasActivatorConstructor => UseActivator;
        public CompoundTypeAliasComponent[] CompoundTypeAliasArguments {get;}
        public string ReturnValueInitializerMethod { get; }

        public ExpressionSyntax GetObjectCreationExpression(LibraryTypes libraryTypes) => ObjectCreationExpression(TypeSyntax, ArgumentList(), null);

        private TypeSyntax CreateTypeSyntax()
        {
            var simpleName = InvokableGenerator.GetSimpleClassName(InterfaceDescription, _methodDescription);
            return (TypeParameters, Namespace) switch
            {
                ({ Count: > 0 }, { Length: > 0 }) => QualifiedName(ParseName(Namespace), GenericName(Identifier(simpleName), TypeArgumentList(SeparatedList<TypeSyntax>(TypeParameters.Select(p => IdentifierName(p.Name)))))),
                ({ Count: > 0 }, _) => GenericName(Identifier(simpleName), TypeArgumentList(SeparatedList<TypeSyntax>(TypeParameters.Select(p => IdentifierName(p.Name))))),
                (_, { Length: > 0 }) => QualifiedName(ParseName(Namespace), IdentifierName(simpleName)),
                _ => IdentifierName(simpleName),
            };
        }

        private TypeSyntax CreateOpenTypeSyntax()
        {
            var simpleName = InvokableGenerator.GetSimpleClassName(InterfaceDescription, _methodDescription);
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