using System.Reflection;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class InterfaceToImplementationMappingCacheTests
{
    [Fact]
    public void GetMethods_ClassWithInheritedGrainInterfaces_ReturnsInterfaceMethods()
    {
        var methods = GrainInterfaceUtils.GetMethods(typeof(ImplicitImplementation));

        Assert.Contains(typeof(IBaseInterface).GetMethod(nameof(IBaseInterface.BaseMethod))!, methods);
        Assert.Contains(typeof(IDerivedInterface).GetMethod(nameof(IDerivedInterface.DerivedMethod))!, methods);
    }

    [Fact]
    public void GetOrCreate_ExplicitInheritedImplementations_MapsDeclaringInterfaces()
    {
        var cache = new InterfaceToImplementationMappingCache();
        var mapping = cache.GetOrCreate(typeof(ExplicitImplementation), typeof(IDerivedInterface));
        var baseMethod = typeof(IBaseInterface).GetMethod(nameof(IBaseInterface.BaseMethod))!;
        var derivedMethod = typeof(IDerivedInterface).GetMethod(nameof(IDerivedInterface.DerivedMethod))!;

        AssertMapping(mapping, baseMethod);
        AssertMapping(mapping, derivedMethod);
        Assert.Same(mapping, cache.GetOrCreate(typeof(ExplicitImplementation), typeof(IDerivedInterface)));
    }

    [Fact]
    public void GetOrCreate_UnimplementedInterface_ThrowsInvalidOperationException()
    {
        var cache = new InterfaceToImplementationMappingCache();

        Assert.Throws<InvalidOperationException>(() => cache.GetOrCreate(typeof(UnrelatedImplementation), typeof(IBaseInterface)));
    }

    [Fact]
    public void GetOrCreate_NonInterfaceType_ThrowsArgumentException()
    {
        var cache = new InterfaceToImplementationMappingCache();

        var exception = Assert.Throws<ArgumentException>(() => cache.GetOrCreate(typeof(UnrelatedImplementation), typeof(object)));

        Assert.Equal("interfaceType", exception.ParamName);
        Assert.Contains("is not an interface", exception.Message);
    }

    private static void AssertMapping(
        Dictionary<MethodInfo, InterfaceToImplementationMappingCache.Entry> mapping,
        MethodInfo interfaceMethod)
    {
        var entry = Assert.Contains(interfaceMethod, mapping);

        Assert.Same(interfaceMethod, entry.InterfaceMethod);
        Assert.Equal(typeof(ExplicitImplementation), entry.ImplementationMethod.DeclaringType);
        Assert.True(entry.ImplementationMethod.IsPrivate);
    }

    public interface IBaseInterface : IAddressable
    {
        Task BaseMethod();
    }

    public interface IDerivedInterface : IBaseInterface
    {
        Task DerivedMethod();
    }

    private sealed class ImplicitImplementation : IDerivedInterface
    {
        public Task BaseMethod() => Task.CompletedTask;

        public Task DerivedMethod() => Task.CompletedTask;
    }

    private sealed class ExplicitImplementation : IDerivedInterface
    {
        Task IBaseInterface.BaseMethod() => Task.CompletedTask;

        Task IDerivedInterface.DerivedMethod() => Task.CompletedTask;
    }

    private sealed class UnrelatedImplementation
    {
    }
}
