using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class RuntimeVersionTests
{
    [Fact]
    public void GetVersionUsesInformationalVersionMetadataForFilelessAssembly()
    {
        var assembly = CreateAssembly(
            new Version(1, 2, 3, 4),
            informationalVersion: "9.8.7+metadata",
            isDebug: true);

        var result = RuntimeVersion.GetVersion(assembly);

        Assert.Equal("9.8.7+metadata (Debug).", result);
    }

    [Fact]
    public void GetVersionUsesAssemblyVersionWhenInformationalVersionIsUnavailable()
    {
        var assembly = CreateAssembly(new Version(1, 2, 3, 4));

        var result = RuntimeVersion.GetVersion(assembly);

        Assert.Equal("1.2.3.4", result);
    }

    private static Assembly CreateAssembly(
        Version version,
        string? informationalVersion = null,
        bool isDebug = false)
    {
        var name = new AssemblyName($"RuntimeVersionTests_{Guid.NewGuid():N}") { Version = version };
        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        if (informationalVersion is not null)
        {
            var constructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!;
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [informationalVersion]));
        }

        if (isDebug)
        {
            var constructor = typeof(DebuggableAttribute).GetConstructor([typeof(DebuggableAttribute.DebuggingModes)])!;
            var modes = DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations;
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [modes]));
        }

        return assembly;
    }
}
