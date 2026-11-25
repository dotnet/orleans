using Orleans;

namespace Orleans.CodeGenerator.Tests.ReferencedTaggedUnions
{
    // The SDK compiles these producer models; generator tests consume their metadata using the Roslyn version referenced by each test target.
    [GenerateSerializer]
    public union ReferencedShallowUnion(int, string);

    [GenerateSerializer]
    public union ReferencedDeepUnion(List<int>, int);

    [Immutable, GenerateSerializer]
    public union ReferencedImmutableUnion(List<int>);

    public interface IReferencedUnionHolder
    {
        ReferencedShallowUnion Shallow { get; set; }
        ReferencedDeepUnion Deep { get; set; }
        ReferencedDeepUnion Alias { get; set; }
        ReferencedImmutableUnion Immutable { get; set; }
    }
}

#if !NET11_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    internal sealed class UnionAttribute : Attribute { }

    internal interface IUnion
    {
        object? Value { get; }
    }
}
#endif
