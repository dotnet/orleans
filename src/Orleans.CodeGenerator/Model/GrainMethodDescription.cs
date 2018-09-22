using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model
{
    internal class GrainMethodDescription
    {
        public GrainMethodDescription(int methodId, IMethodSymbol method)
        {
            this.MethodId = methodId;
            this.Method = method;
        }

        public int MethodId { get; }
        public IMethodSymbol Method { get; }
    }
}