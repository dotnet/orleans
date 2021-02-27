using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal class GeneratedProxyDescription
    {
        public GeneratedProxyDescription(InvokableInterfaceDescription interfaceDescription)
        {
            InterfaceDescription = interfaceDescription;
            TypeSyntax = GetProxyTypeName(interfaceDescription);
        }

        public TypeSyntax TypeSyntax { get; }
        public InvokableInterfaceDescription InterfaceDescription { get; }

        private static TypeSyntax GetProxyTypeName(InvokableInterfaceDescription interfaceDescription)
        {
            var interfaceType = interfaceDescription.InterfaceType;
            var genericArity = interfaceType.GetAllTypeParameters().Count();
            var name = ProxyGenerator.GetSimpleClassName(interfaceDescription);
            if (genericArity > 0)
            {
                name += $"<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(interfaceDescription.GeneratedNamespace + "." + name);
        }
    }
}