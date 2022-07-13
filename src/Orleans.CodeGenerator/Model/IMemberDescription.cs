using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;

namespace Orleans.CodeGenerator
{
    internal interface IMemberDescription
    {
        ushort FieldId { get; }
        ISymbol Symbol { get; }
        ITypeSymbol Type { get; }
        INamedTypeSymbol ContainingType { get; }
        string AssemblyName { get; }
        string TypeName { get; }
        TypeSyntax TypeSyntax { get; }
        string TypeNameIdentifier { get; }
        TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol);
        bool IsPrimaryConstructorParameter { get; } 
    }

    internal sealed class MemberDescriptionTypeComparer : IEqualityComparer<IMemberDescription>
    {
        public static MemberDescriptionTypeComparer Default { get; } = new MemberDescriptionTypeComparer();

        public bool Equals(IMemberDescription x, IMemberDescription y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (ReferenceEquals(x, null) || ReferenceEquals(y, null))
            {
                return false;
            }

           return string.Equals(x.TypeName, y.TypeName) && string.Equals(x.AssemblyName, y.AssemblyName);
        }

        public int GetHashCode(IMemberDescription obj)
        {
            int hashCode = -499943048;
            hashCode = hashCode * -1521134295 + StringComparer.Ordinal.GetHashCode(obj.TypeName);
            hashCode = hashCode * -1521134295 + StringComparer.Ordinal.GetHashCode(obj.AssemblyName);
            return hashCode;
        }
    }
}