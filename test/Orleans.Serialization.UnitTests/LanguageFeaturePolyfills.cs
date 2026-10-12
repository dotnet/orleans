#if !NET11_0_OR_GREATER
// The C# compiler requires these types to declare closed types and unions. They are part of the base class library starting with .NET 11.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class IsClosedTypeAttribute : Attribute
    {
        public Type[]? DerivedTypes { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    internal sealed class UnionAttribute : Attribute
    {
    }

    internal interface IUnion
    {
        object? Value { get; }
    }
}
#endif
