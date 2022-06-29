using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal class FieldDescription : IFieldDescription
    {
        public FieldDescription(ushort fieldId, bool isPrimaryConstructorParameter, IFieldSymbol member)
        {
            FieldId = fieldId;
            IsPrimaryConstructorParameter = isPrimaryConstructorParameter;
            Field = member;
            Type = member.Type;
            ContainingType = member.ContainingType;

            if (Type.TypeKind == TypeKind.Dynamic)
            {
                TypeSyntax = PredefinedType(Token(SyntaxKind.ObjectKeyword));
            }
            else
            {
                TypeSyntax = Type.ToTypeSyntax();
            }
        }

        public ISymbol Symbol => Field;
        public IFieldSymbol Field { get; }
        public ushort FieldId { get; }
        public ITypeSymbol Type { get; }
        public INamedTypeSymbol ContainingType { get; }
        public TypeSyntax TypeSyntax { get; }

        public string AssemblyName => Type.ContainingAssembly.ToDisplayName();
        public string TypeName => Type.ToDisplayName();
        public string TypeNameIdentifier => Type.GetValidIdentifier();
        public bool IsPrimaryConstructorParameter { get; set; }

        public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax();
    }

    internal interface IFieldDescription : IMemberDescription
    {
        IFieldSymbol Field { get; }
    }
}