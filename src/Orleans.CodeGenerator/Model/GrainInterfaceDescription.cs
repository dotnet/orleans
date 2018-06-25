using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Generators;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Model
{
    internal class GrainInterfaceDescription : ITypeDescription
    {
        public GrainInterfaceDescription(INamedTypeSymbol type, int interfaceId, ushort interfaceVersion, IEnumerable<GrainMethodDescription> members)
        {
            this.Type = type;
            this.InterfaceId = interfaceId;
            this.InterfaceVersion = interfaceVersion;
            this.Methods = members.ToList();
        }

        public ushort InterfaceVersion { get; }

        public int InterfaceId { get; }

        public INamedTypeSymbol Type { get; }

        public List<GrainMethodDescription> Methods { get; }

        public string InvokerTypeName => GrainMethodInvokerGenerator.GetGeneratedClassName(this.Type);
        public string ReferenceTypeName => GrainReferenceGenerator.GetGeneratedClassName(this.Type);

        public TypeSyntax InvokerType => ParseTypeName(this.Type.GetParsableReplacementName(this.InvokerTypeName));
        public TypeSyntax ReferenceType => ParseTypeName(this.Type.GetParsableReplacementName(this.ReferenceTypeName));
    }
}