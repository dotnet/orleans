using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal interface IPropertyDescription : IMemberDescription 
    {
    }

    internal class PropertyDescription : IPropertyDescription
    {
        public PropertyDescription(uint fieldId, bool isPrimaryConstructorParameter, IPropertySymbol property)
        {
            FieldId = fieldId;
            IsPrimaryConstructorParameter = isPrimaryConstructorParameter;
            Property = property;

            if (Type.TypeKind == TypeKind.Dynamic)
            {
                TypeSyntax = PredefinedType(Token(SyntaxKind.ObjectKeyword));
            }
            else
            {
                TypeSyntax = Type.ToTypeSyntax();
            }
        }

        public uint FieldId { get; }
        public ISymbol Symbol => Property;
        public ITypeSymbol Type => Property.Type;
        public INamedTypeSymbol ContainingType => Property.ContainingType;
        public IPropertySymbol Property { get; }

        public TypeSyntax TypeSyntax { get; }

        public string AssemblyName => Type.ContainingAssembly.ToDisplayName();
        public string TypeName => Type.ToDisplayName();
        public string TypeNameIdentifier => Type.GetValidIdentifier();
        public bool IsPrimaryConstructorParameter { get; set; }

        public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax();
    }
}