using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal class FieldDescription : IFieldDescription
    {
        public FieldDescription(ushort fieldId, IFieldSymbol member, ITypeSymbol type)
        {
            FieldId = fieldId;
            Field = member;
            Type = type;

            if (Type.TypeKind == TypeKind.Dynamic)
            {
                TypeSyntax = PredefinedType(Token(SyntaxKind.ObjectKeyword));
            }
            else
            {
                TypeSyntax = Type.ToTypeSyntax();
            }
        }

        public IFieldSymbol Field { get; }
        public ushort FieldId { get; }
        public ITypeSymbol Type { get; }
        public TypeSyntax TypeSyntax { get; }

        public string AssemblyName => Type.ContainingAssembly.ToDisplayName();
        public string TypeName => Type.ToDisplayName();
        public string TypeNameIdentifier => Type.GetValidIdentifier();

        public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax();
    }

    internal interface IFieldDescription : IMemberDescription
    {
        IFieldSymbol Field { get; }
    }
}