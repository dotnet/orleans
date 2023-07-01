using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator
{
    internal interface ISerializableTypeDescription
    {
        Accessibility Accessibility { get; }
        TypeSyntax TypeSyntax { get; }
        bool HasComplexBaseType { get; }
        bool IncludePrimaryConstructorParameters { get; }
        INamedTypeSymbol BaseType { get; }
        TypeSyntax BaseTypeSyntax { get; }
        string Namespace { get; }
        string GeneratedNamespace { get; }
        string Name { get; }
        bool IsValueType { get; }
        bool IsSealedType { get; }
        bool IsAbstractType { get; }
        bool IsEnumType { get; }
        bool IsGenericType { get; }
        List<(string Name, ITypeParameterSymbol Parameter)> TypeParameters { get; }
        List<IMemberDescription> Members { get; }
        SemanticModel SemanticModel { get; }
        bool UseActivator { get; }
        bool IsEmptyConstructable { get; }
        bool HasActivatorConstructor { get; }
        bool TrackReferences { get; }
        bool OmitDefaultMemberValues { get; }
        ExpressionSyntax GetObjectCreationExpression(LibraryTypes libraryTypes);
        List<INamedTypeSymbol> SerializationHooks { get; }
        bool IsShallowCopyable { get; }
        bool IsUnsealedImmutable { get; }
        bool IsImmutable { get; }
        bool IsExceptionType { get; }
        List<TypeSyntax> ActivatorConstructorParameters { get; }
    }
}