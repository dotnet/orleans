using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model
{
    internal class GrainClassDescription : ITypeDescription
    {
        public GrainClassDescription(INamedTypeSymbol type, int typeCode)
        {
            this.Type = type;
            this.TypeCode = typeCode;
        }

        public int TypeCode { get; }

        public INamedTypeSymbol Type { get; }
    }
}