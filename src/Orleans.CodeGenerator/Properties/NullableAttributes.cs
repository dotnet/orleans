#if NETSTANDARD2_0
namespace System.Diagnostics.CodeAnalysis;

[global::System.AttributeUsage(global::System.AttributeTargets.Parameter, Inherited = false)]
internal sealed class NotNullWhenAttribute(bool returnValue) : global::System.Attribute
{
    public bool ReturnValue { get; } = returnValue;
}
#endif
