#if NETSTANDARD2_1
namespace System.Diagnostics.CodeAnalysis;

[AttributeUsage(
    AttributeTargets.Constructor | AttributeTargets.Event | AttributeTargets.Method | AttributeTargets.Property,
    Inherited = false,
    AllowMultiple = false)]
internal sealed class RequiresAssemblyFilesAttribute : Attribute
{
    public RequiresAssemblyFilesAttribute()
    {
    }

    public RequiresAssemblyFilesAttribute(string message)
    {
        Message = message;
    }

    public string? Message { get; }

    public string? Url { get; set; }
}
#endif
