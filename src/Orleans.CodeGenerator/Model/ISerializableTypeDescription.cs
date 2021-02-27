using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;

namespace Orleans.CodeGenerator
{
    internal interface ISerializableTypeDescription
    {
        Accessibility Accessibility { get; }
        TypeSyntax TypeSyntax { get; }
        bool HasComplexBaseType { get; }
        INamedTypeSymbol BaseType { get; }
        TypeSyntax BaseTypeSyntax { get; }
        string Namespace { get; }
        string GeneratedNamespace { get; }
        string Name { get; }
        bool IsValueType { get; }
        bool IsSealedType { get; }
        bool IsEnumType { get; }
        bool IsGenericType { get; }
        List<(string Name, ITypeParameterSymbol Parameter)> TypeParameters { get; }
        List<IMemberDescription> Members { get; }
        SemanticModel SemanticModel { get; }
        bool UseActivator { get; }
        bool IsEmptyConstructable { get; }
        bool HasActivatorConstructor { get; }
        bool IsPartial { get; }
        bool TrackReferences { get; }
        bool OmitDefaultMemberValues { get; }
        ExpressionSyntax GetObjectCreationExpression(LibraryTypes libraryTypes);
        List<INamedTypeSymbol> SerializationHooks { get; }
        bool IsImmutable { get; }
        List<TypeSyntax> ActivatorConstructorParameters { get; }
    }
}