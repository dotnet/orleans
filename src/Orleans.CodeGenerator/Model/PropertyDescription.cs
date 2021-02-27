using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal class PropertyDescription : IMemberDescription
    {
        public PropertyDescription(ushort fieldId, IPropertySymbol property)
        {
            FieldId = fieldId;
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

        public ushort FieldId { get; }
        public ISymbol Member => Property;
        public ITypeSymbol Type => Property.Type;
        public IPropertySymbol Property { get; }

        public TypeSyntax TypeSyntax { get; }

        public string AssemblyName => Type.ContainingAssembly.ToDisplayName();
        public string TypeName => Type.ToDisplayName();
        public string TypeNameIdentifier => Type.GetValidIdentifier();

        public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax();
    }
}