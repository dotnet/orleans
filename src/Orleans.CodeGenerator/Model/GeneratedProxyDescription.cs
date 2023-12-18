using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal class GeneratedProxyDescription
    {
        public GeneratedProxyDescription(ProxyInterfaceDescription interfaceDescription, string generatedClassName)
        {
            InterfaceDescription = interfaceDescription;
            GeneratedClassName = generatedClassName;
            TypeSyntax = GetProxyTypeName(interfaceDescription);
            if (InterfaceDescription.TypeParameters.Count == 0)
            {
                MetadataName = $"{InterfaceDescription.GeneratedNamespace}.{GeneratedClassName}";
            }
            else
            {
                MetadataName =  $"{InterfaceDescription.GeneratedNamespace}.{GeneratedClassName}`{InterfaceDescription.TypeParameters.Count}";
            }
        }

        public TypeSyntax TypeSyntax { get; }
        public ProxyInterfaceDescription InterfaceDescription { get; }
        public string GeneratedClassName { get; }
        public string MetadataName { get; }

        private static TypeSyntax GetProxyTypeName(ProxyInterfaceDescription interfaceDescription)
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